using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

public sealed partial class Updater
{
    /// <summary>Null when the verified binary is staged next to the installed one; otherwise why it was refused.</summary>
    private async Task<UpdateResult?> DownloadAndStageAsync(UpdateOffer offer, string staged, CancellationToken ct)
    {
        if (!Uri.TryCreate(offer.Url, UriKind.Absolute, out var url) || downloads.BaseAddress is not { } server
            || url.GetLeftPart(UriPartial.Authority) != server.GetLeftPart(UriPartial.Authority)
            || !url.AbsolutePath.StartsWith("/" + UpdatePaths.DownloadsFolder + "/", StringComparison.Ordinal))
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
        // Decided by the OS the build was signed for (the host's own outside tests): codesign exists only on macOS.
        if (config.UpdateOs == OsKinds.MacOs && runner.Run("/usr/bin/codesign", ["--verify", "--strict", staged], config.UpdateProbeTimeout).ExitCode != 0)
        {
            return new(UpdateCodes.BadBinary, "the binary carries no valid macOS code signature");
        }

        var (exit, output2) = runner.Run(staged, ["version"], config.UpdateProbeTimeout);
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

        output.Flush(flushToDisk: true); // a power loss right after the rename must not leave a torn binary
        return null;
    }
}
