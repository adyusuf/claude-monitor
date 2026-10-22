using System.Text.Json;
using Podium.Api.Config;
using Podium.Contracts;

namespace Podium.Api.Update;

/// <summary>
/// The agent builds this server hands out: <c>downloads/manifest.json</c>, written and signed at release time by
/// scripts/sign_manifest.py (one entry per channel, version, OS and CPU). The server cannot sign; it chooses the newest
/// entry and builds the download address from its own public origin, so a build of another channel is never offered:
/// the channel is whatever this server's deploy put in its web root. With AIPOD_DOWNLOADS_URL the manifest is read from that
/// download host instead (<see cref="RefreshAsync"/> keeps a copy) and the offered addresses point there; the channel is then the
/// folder the address names.
/// </summary>
public sealed class UpdateCatalog(ApiConfig config, HttpMessageHandler? handler = null)
{
    /// <summary>A manifest is a few KiB; anything near this is not one.</summary>
    private const int RemoteManifestMax = 256 * 1024;

    private readonly object gate = new();
    private (DateTime Stamp, long Length, IReadOnlyList<Entry> Entries)? cached;
    private readonly HttpClient? remote = config.DownloadsUrl is null
        ? null
        : new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private volatile IReadOnlyList<Entry> remoteEntries = [];

    /// <summary>True when the builds are published on a download host, not on this server.</summary>
    public bool IsRemote => remote is not null;

    /// <summary>
    /// Reads the download host's manifest.json into memory. A host that does not answer, or answers with something that is not a
    /// manifest, leaves the last good copy in place: a store outage must not withdraw an update that was on offer.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (remote is null) return;
        try
        {
            using var response = await remote.GetAsync($"{config.DownloadsUrl}/manifest.json", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > RemoteManifestMax) return;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[RemoteManifestMax + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), ct)) > 0) length += read;
            if (length > RemoteManifestMax || Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, length)) is not { } entries) return;
            remoteEntries = entries;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException)
        {
            // keep the last good copy; the next refresh tries again
        }
    }

    /// <summary>The newest build for this OS and CPU, or null when none is published.</summary>
    public UpdateOffer? Latest(string os, string arch)
    {
        Entry? best = null;
        Version? bestVersion = null;
        foreach (var e in Load())
        {
            if (!string.Equals(e.Os, os, StringComparison.Ordinal) || !string.Equals(e.Arch, arch, StringComparison.Ordinal)) continue;
            if (!UpdateVersion.TryParse(e.Version, out var v) || (bestVersion is not null && v <= bestVersion)) continue;
            best = e;
            bestVersion = v;
        }

        return best is null
            ? null
            : new UpdateOffer(best.Version!, $"{config.DownloadsUrl ?? $"{config.PublicOrigin}/{ApiConfig.DownloadsFolder}"}/{best.File}", best.Sha256!, best.Signature!,
                best.MinSupported!, best.Channel!);
    }

    private IReadOnlyList<Entry> Load()
    {
        if (remote is not null) return remoteEntries;
        if (config.DownloadsDir is not { } dir) return [];
        var info = new FileInfo(Path.Combine(dir, "manifest.json"));
        if (!info.Exists) return [];
        lock (gate)
        {
            if (cached is { } c && c.Stamp == info.LastWriteTimeUtc && c.Length == info.Length) return c.Entries;
            IReadOnlyList<Entry> entries;
            try
            {
                entries = Parse(File.ReadAllText(info.FullName)) ?? [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                entries = []; // a half-written or unreadable file offers nothing; the next change of the file is read again
            }

            cached = (info.LastWriteTimeUtc, info.Length, entries);
            return entries;
        }
    }

    /// <summary>The usable entries of a manifest text; null when it is not a manifest of this format (unreadable, half-written, another format).</summary>
    private static IReadOnlyList<Entry>? Parse(string text)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(text, JsonSerializerOptions.Web);
            return manifest?.Format == UpdateManifest.Format ? [.. (manifest.Entries ?? []).Where(IsUsable)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every field is present, the file name is a plain name (it becomes part of a URL), the hash is hex and the versions are the strict x.y.z the agent also requires (else a lax entry could hide a valid one).</summary>
    private static bool IsUsable(Entry e) =>
        new[] { e.Channel, e.Version, e.Os, e.Arch, e.File, e.Sha256, e.MinSupported, e.Signature }.All(v => !string.IsNullOrEmpty(v))
        && UpdateUrls.IsPlainFile(e.File)
        && e.Sha256!.Length == 64 && e.Sha256.All(char.IsAsciiHexDigit)
        && UpdateVersion.TryParse(e.Version, out _) && UpdateVersion.TryParse(e.MinSupported, out _);

    private sealed record Manifest(string? Format, List<Entry>? Entries);
    private sealed record Entry(string? Channel, string? Version, string? Os, string? Arch, string? File, string? Sha256, string? MinSupported,
        string? Signature);
}
