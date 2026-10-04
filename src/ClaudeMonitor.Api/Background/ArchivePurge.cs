using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Background;

/// <summary>
/// Removes given sessions' events from the zipped day files the archiver wrote (account deletion: the captured content
/// of the deleted user's sessions must leave the archives too). Each affected file is rewritten without those events
/// under a temporary name and then moved over the old one; its event_archives row gets the new count, size and SHA-256.
/// A file left with no event is deleted together with its row.
/// </summary>
public static class ArchivePurge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Returns how many archive files were rewritten or removed.</summary>
    public static async Task<int> RemoveSessionsAsync(MonitorDb db, IReadOnlyCollection<Guid> sessionIds,
        IReadOnlyCollection<Guid> workspaceIds, DateOnly since, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0) return 0;
        var drop = sessionIds.Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archives = await db.EventArchives.Where(a => workspaceIds.Contains(a.WorkspaceId) && a.Day >= since).ToListAsync(ct);
        var changed = 0;
        foreach (var archive in archives)
        {
            if (!File.Exists(archive.Path)) continue;
            var (kept, removed, entryName) = await ReadAsync(archive.Path, drop, ct);
            if (removed == 0) continue;
            changed++;
            if (kept.Count == 0)
            {
                File.Delete(archive.Path);
                db.EventArchives.Remove(archive);
                continue;
            }

            var temp = archive.Path + ".purge";
            await using (var stream = File.Create(temp))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                await JsonSerializer.SerializeAsync(entryStream, kept, Json, ct);
            }

            File.Move(temp, archive.Path, overwrite: true);
            archive.EventCount = kept.Count;
            archive.Bytes = new FileInfo(archive.Path).Length;
            await using var file = File.OpenRead(archive.Path);
            archive.Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    private static async Task<(List<JsonElement> Kept, int Removed, string EntryName)> ReadAsync(string path, HashSet<string> drop,
        CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.Single();
        await using var stream = entry.Open();
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var kept = new List<JsonElement>();
        var removed = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var session = e.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            if (session is not null && drop.Contains(session)) removed++;
            else kept.Add(e.Clone());
        }

        return (kept, removed, entry.Name);
    }
}
