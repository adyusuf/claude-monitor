using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

/// <summary>What a check found. <see cref="Offer"/> is set only for a build that passed every check below and is newer.</summary>
public sealed record UpdateCheck(string Code, string Message, UpdateOffer? Offer = null, bool BelowMinimum = false)
{
    public bool Refused => Code is not (UpdateCodes.UpToDate or UpdateCodes.Available);
}

public sealed record UpdateResult(string Code, string Message, string? From = null, string? To = null)
{
    public bool Succeeded => Code == UpdateCodes.Installed;
}

/// <summary>
/// Self-update (ADR-0004). A build is taken only when ALL of these hold, and every refusal is recorded (agent.log and
/// `cm-agent status`), never swallowed: the offer's signature verifies against the key built into this agent for this agent's
/// own channel, OS and CPU; its version is strictly higher than the running one; the download comes from the server's own
/// origin; its SHA-256 equals the signed one; it is a zip of exactly one file that, started with `version`, reports the offered
/// version; and on macOS it carries a valid code signature. Then the binary is replaced (keeping the previous one), the daemon
/// restarted, and if the new daemon is not answered by the API in time the previous binary is put back.
/// </summary>
public sealed class Updater(AgentConfig config, ApiClient api, HttpClient downloads, IProcessRunner runner, IDaemonControl daemon,
    AgentLog log, TimeProvider clock)
{
    /// <summary>Asks the server for the newest build and decides whether this agent may take it. Changes nothing on disk but update-state.json.</summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        UpdateCheck result;
        try
        {
            result = await EvaluateAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or ApiException)
        {
            result = new UpdateCheck(UpdateCodes.Unreachable, $"the server could not be asked: {e.GetType().Name}");
        }

        UpdateState.Change(config, s => s with { CheckedAt = Now(), Result = result.Code, Detail = result.Message, Available = result.Offer?.Version });
        if (result.Refused) log.Write($"update refused: {result.Code}: {result.Message}");
        return result;
    }

    private async Task<UpdateCheck> EvaluateAsync(CancellationToken ct)
    {
        if (config.UpdatePublicKey.Length == 0) return new(UpdateCodes.NoKey, "this build has no update key built in");
        if (config.UpdateOs == "unsupported") return new(UpdateCodes.Unsupported, "this operating system has no agent builds");
        var offer = await api.LatestAsync(config.UpdateOs, config.UpdateArch, ct);
        if (offer is null) return new(UpdateCodes.UpToDate, "no update is published for this machine");
        if (offer.Channel != config.UpdateChannel) return new(UpdateCodes.Channel, $"the offer is for channel \"{offer.Channel}\", this agent is \"{config.UpdateChannel}\"");
        if (!UpdateRules.TryParse(offer.Version, out _) || !UpdateRules.TryParse(offer.MinSupported, out _)
            || offer.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit) || string.IsNullOrEmpty(offer.Signature))
        {
            return new(UpdateCodes.Malformed, "the offer's version, hash or signature is malformed");
        }

        string payload;
        try
        {
            payload = UpdateManifest.Payload(config.UpdateChannel, offer.Version, config.UpdateOs, config.UpdateArch, hash.ToLowerInvariant(), offer.MinSupported);
        }
        catch (ArgumentException)
        {
            return new(UpdateCodes.Malformed, "the offer holds an empty field or a line break");
        }

        if (!UpdateManifest.Verify(config.UpdatePublicKey, payload, offer.Signature))
        {
            return new(UpdateCodes.BadSignature, $"the signature of {offer.Version} does not verify against this agent's {config.UpdateChannel} key");
        }

        var below = UpdateRules.IsBelowMinimum(offer.MinSupported, AgentConfig.Version);
        if (UpdateRules.IsNewer(offer.Version, AgentConfig.Version)) return new(UpdateCodes.Available, $"{offer.Version} is available", offer, below);
        return offer.Version == AgentConfig.Version
            ? new(UpdateCodes.UpToDate, "this is the newest version")
            : new(UpdateCodes.Downgrade, $"the offered {offer.Version} is older than the running {AgentConfig.Version}; never installed");
    }

    /// <summary>Installs an offer that <see cref="CheckAsync"/> accepted: download, verify, replace, restart, watch, roll back if unhealthy.</summary>
    public async Task<UpdateResult> ApplyAsync(UpdateOffer offer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        config.EnsureHome();
        using var guard = DaemonHost.TryLock(config.UpdateLockPath);
        if (guard is null) return Record(new(UpdateCodes.Busy, "another update is running"));
        if (!File.Exists(config.BinaryPath)) return Record(new(UpdateCodes.NotInstalled, $"no installed binary at {config.BinaryPath} (run cm-agent install)"));
        var staged = BinarySwap.Staged(config.BinaryPath);
        try
        {
            UpdateState.Change(config, s => s with { AttemptedAt = Now(), Phase = null });
            if (await DownloadAndStageAsync(offer, staged, ct) is { } refused) return Record(refused);
            return await ReplaceAndWatchAsync(offer, staged, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException or InvalidDataException)
        {
            return Record(new(UpdateCodes.Failed, $"{e.GetType().Name}: {e.Message}"));
        }
        finally
        {
            Cleanup(staged);
        }
    }

    /// <summary>Null when the verified binary is staged next to the installed one; otherwise why it was refused.</summary>
    private async Task<UpdateResult?> DownloadAndStageAsync(UpdateOffer offer, string staged, CancellationToken ct)
    {
        if (!Uri.TryCreate(offer.Url, UriKind.Absolute, out var url) || downloads.BaseAddress is not { } server
            || url.GetLeftPart(UriPartial.Authority) != server.GetLeftPart(UriPartial.Authority)
            || !url.AbsolutePath.StartsWith("/downloads/", StringComparison.Ordinal))
        {
            return new(UpdateCodes.BadUrl, "the download address is not on this agent's own server");
        }

        Directory.CreateDirectory(config.UpdateDir);
        var zip = Path.Combine(config.UpdateDir, "download.zip");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(config.UpdateDownloadTimeout);
            using var response = await downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK) return new(UpdateCodes.Failed, $"the download answered {(int)response.StatusCode}");
            if (response.RequestMessage?.RequestUri != url) return new(UpdateCodes.BadUrl, "the download was redirected");
            if (response.Content.Headers.ContentLength > config.UpdateDownloadMax) return new(UpdateCodes.TooLarge, "the download is larger than allowed");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > config.UpdateDownloadMax) return new(UpdateCodes.TooLarge, "the download is larger than allowed");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
            }

            if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), offer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(UpdateCodes.HashMismatch, "the download's SHA-256 is not the signed one");
            }
        }

        if (ExtractBinary(zip, staged) is { } bad) return bad;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        if (OperatingSystem.IsMacOS() && runner.Run("codesign", ["--verify", "--strict", staged], TimeSpan.FromSeconds(30)).ExitCode != 0)
        {
            return new(UpdateCodes.BadBinary, "the binary carries no valid macOS code signature");
        }

        var (exit, output2) = runner.Run(staged, ["version"], TimeSpan.FromSeconds(30));
        return exit == 0 && output2.Trim() == offer.Version
            ? null
            : new(UpdateCodes.BadBinary, "the downloaded binary does not run, or is not the version it was signed as");
    }

    /// <summary>Exactly one entry, with the binary's own name and a plain path; its size is capped while it is copied.</summary>
    private UpdateResult? ExtractBinary(string zip, string target)
    {
        var name = Path.GetFileName(config.BinaryPath);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != name)
        {
            return new(UpdateCodes.BadArchive, $"the archive must hold exactly one file, {name}");
        }

        var entry = archive.Entries[0];
        if (entry.Length > config.UpdateBinaryMax) return new(UpdateCodes.TooLarge, "the binary in the archive is larger than allowed");
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > config.UpdateBinaryMax) return new(UpdateCodes.TooLarge, "the binary in the archive is larger than allowed");
            output.Write(buffer, 0, read);
        }

        return null;
    }

    private async Task<UpdateResult> ReplaceAndWatchAsync(UpdateOffer offer, string staged, CancellationToken ct)
    {
        var from = AgentConfig.Version;
        var style = BinarySwap.Native;
        var wasRunning = daemon.IsRunning();
        var connected = Auth.Identity.Load(config).Connected;
        var swapped = clock.GetUtcNow();
        BinarySwap.Install(config.BinaryPath, staged, style);
        UpdateState.Change(config, s => s with { Phase = UpdateState.PendingHealth, From = from, To = offer.Version, Result = UpdateCodes.Available });
        log.Write($"update: {from} -> {offer.Version} put in place, restarting the daemon");

        if (!wasRunning && !connected) return Record(Installed(from, offer.Version, "replaced; no daemon to restart"));
        if (wasRunning && !await daemon.StopAsync(config.UpdateStopWait, ct))
        {
            return Record(Installed(from, offer.Version, "replaced; the running daemon did not stop and keeps the old version until it restarts"));
        }

        daemon.Start(config.BinaryPath);
        if (await WaitHealthyAsync(offer.Version, swapped, ct)) return Record(Installed(from, offer.Version, "the new daemon is healthy"));

        log.Write($"update: {offer.Version} is not healthy after {config.UpdateHealthWait.TotalSeconds:0} s, rolling back to {from}");
        await daemon.StopAsync(config.UpdateStopWait, ct);
        BinarySwap.Restore(config.BinaryPath, style);
        daemon.Start(config.BinaryPath);
        UpdateState.Change(config, s => s with { Phase = null, BlockedVersion = offer.Version, To = offer.Version, From = from });
        return Record(new(UpdateCodes.RolledBack, $"{offer.Version} did not become healthy within {config.UpdateHealthWait.TotalSeconds:0} s; back on {from}", from, offer.Version));
    }

    private async Task<bool> WaitHealthyAsync(string version, DateTimeOffset since, CancellationToken ct)
    {
        using var store = new LocalStore(config.DatabasePath);
        var until = clock.GetUtcNow() + config.UpdateHealthWait;
        while (true)
        {
            if (UpdateHealth.IsHealthy(store, version, since)) return true;
            if (clock.GetUtcNow() >= until) return false;
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
    }

    private static UpdateResult Installed(string from, string to, string note) => new(UpdateCodes.Installed, $"{from} -> {to}: {note}", from, to);

    /// <summary>Writes the outcome to update-state.json and agent.log and passes it on.</summary>
    private UpdateResult Record(UpdateResult result)
    {
        UpdateState.Change(config, s => s with
        {
            Result = result.Code,
            Detail = result.Message,
            Phase = null,
            Available = result.Succeeded ? null : s.Available,
            InstalledAt = result.Succeeded ? Now() : s.InstalledAt,
        });
        log.Write($"update {result.Code}: {result.Message}");
        return result;
    }

    private void Cleanup(string staged)
    {
        try
        {
            File.Delete(staged);
            if (Directory.Exists(config.UpdateDir)) Directory.Delete(config.UpdateDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the next update starts with a clean folder anyway
        }
    }

    private string Now() => clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
