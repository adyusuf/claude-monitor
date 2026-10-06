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
public sealed partial class Updater(AgentConfig config, ApiClient api, HttpClient downloads, IProcessRunner runner, IDaemonControl daemon,
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

    /// <summary>Text the server sent that has not been verified (it may hold a line break or a terminal escape): only plain characters, and short.</summary>
    internal static string Printable(string? text) =>
        new((text ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(32).ToArray());

    private async Task<UpdateCheck> EvaluateAsync(CancellationToken ct)
    {
        if (config.UpdatePublicKey.Length == 0) return new(UpdateCodes.NoKey, "this build has no update key built in");
        if (config.UpdateOs == AgentConfig.Unsupported) return new(UpdateCodes.Unsupported, "this operating system has no agent builds");
        var offer = await api.LatestAsync(config.UpdateOs, config.UpdateArch, ct);
        return offer is null ? new(UpdateCodes.UpToDate, "no update is published for this machine") : Judge(offer);
    }

    /// <summary>
    /// Every test an offer must pass before anything is downloaded; <see cref="ApplyAsync"/> runs it again, so an offer that
    /// did not come through <see cref="CheckAsync"/> can never be installed. Pure: no network, no disk.
    /// </summary>
    internal UpdateCheck Judge(UpdateOffer offer)
    {
        if (config.UpdatePublicKey.Length == 0) return new(UpdateCodes.NoKey, "this build has no update key built in");
        if (offer.Channel != config.UpdateChannel)
        {
            return new(UpdateCodes.Channel, $"the offer is for channel \"{Printable(offer.Channel)}\", this agent is \"{config.UpdateChannel}\"");
        }

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
        if (Judge(offer) is { Offer: null } refused)
        {
            return Record(new(refused.Code, refused.Message)); // not a verified, newer offer: nothing is downloaded, whoever asked
        }

        using var guard = DaemonHost.TryLock(config.UpdateLockPath);
        if (guard is null) return Record(new(UpdateCodes.Busy, "another update is running"));
        if (!File.Exists(config.BinaryPath)) return Record(new(UpdateCodes.NotInstalled, $"no installed binary at {config.BinaryPath} (run cm-agent install)"));
        var staged = BinarySwap.Staged(config.BinaryPath);
        try
        {
            UpdateState.Change(config, s => s with { AttemptedAt = Now(), Phase = null });
            if (await DownloadAndStageAsync(offer, staged, ct) is { } rejected) return Record(rejected);
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
}
