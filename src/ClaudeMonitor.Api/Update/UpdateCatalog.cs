using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Update;

/// <summary>
/// The agent builds this server hands out: <c>downloads/manifest.json</c>, written and signed at release time by
/// scripts/sign_manifest.py (one entry per channel, version, OS and CPU). The server cannot sign; it chooses the newest
/// entry and builds the download address from its own public origin, so a build of another channel is never offered:
/// the channel is whatever this server's deploy put in its web root.
/// </summary>
public sealed class UpdateCatalog(ApiConfig config)
{
    private readonly object gate = new();
    private (DateTime Stamp, long Length, IReadOnlyList<Entry> Entries)? cached;

    /// <summary>The newest build for this OS and CPU, or null when none is published.</summary>
    public UpdateOffer? Latest(string os, string arch)
    {
        Entry? best = null;
        Version? bestVersion = null;
        foreach (var e in Load())
        {
            if (!string.Equals(e.Os, os, StringComparison.Ordinal) || !string.Equals(e.Arch, arch, StringComparison.Ordinal)) continue;
            if (!Version.TryParse(e.Version, out var v) || (bestVersion is not null && v <= bestVersion)) continue;
            best = e;
            bestVersion = v;
        }

        return best is null
            ? null
            : new UpdateOffer(best.Version!, $"{config.PublicOrigin}/{ApiConfig.DownloadsFolder}/{best.File}", best.Sha256!, best.Signature!,
                best.MinSupported!, best.Channel!);
    }

    private IReadOnlyList<Entry> Load()
    {
        if (config.DownloadsDir is not { } dir) return [];
        var info = new FileInfo(Path.Combine(dir, "manifest.json"));
        if (!info.Exists) return [];
        lock (gate)
        {
            if (cached is { } c && c.Stamp == info.LastWriteTimeUtc && c.Length == info.Length) return c.Entries;
            IReadOnlyList<Entry> entries;
            try
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(info.FullName), JsonSerializerOptions.Web);
                entries = manifest?.Format == UpdateManifest.Format ? [.. (manifest.Entries ?? []).Where(IsUsable)] : [];
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                entries = []; // a half-written or unreadable file offers nothing; the next change of the file is read again
            }

            cached = (info.LastWriteTimeUtc, info.Length, entries);
            return entries;
        }
    }

    /// <summary>Every field is present, the file name is a plain name (it becomes part of a URL) and the hash is hex.</summary>
    private static bool IsUsable(Entry e) =>
        new[] { e.Channel, e.Version, e.Os, e.Arch, e.File, e.Sha256, e.MinSupported, e.Signature }.All(v => !string.IsNullOrEmpty(v))
        && e.File!.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_') && !e.File!.StartsWith('.')
        && e.Sha256!.Length == 64 && e.Sha256.All(char.IsAsciiHexDigit);

    private sealed record Manifest(string? Format, List<Entry>? Entries);
    private sealed record Entry(string? Channel, string? Version, string? Os, string? Arch, string? File, string? Sha256, string? MinSupported,
        string? Signature);
}
