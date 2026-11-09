using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Podium.Agent.Access;

/// <summary>
/// The SHA-256 of an executable an access layer lists with a hash (ADR-0020 §4), cached by the file's identity, size and modification time so a program
/// is read once until it changes. Null when the file cannot be read: the hash then matches nothing and the run is refused.
/// </summary>
internal static class ExecutableHashes
{
    private const int MaxCached = 256;
    private static readonly ConcurrentDictionary<(FileId Id, long Size, long Ticks), string> Cache = new();

    public static string? Of(string realPath)
    {
        try
        {
            var info = new FileInfo(realPath);
            if (!info.Exists) return null;
            var key = PathIdentity.Of(realPath) is { } id ? (id, info.Length, info.LastWriteTimeUtc.Ticks) : ((FileId, long, long)?)null;
            if (key is { } k && Cache.TryGetValue(k, out var cached)) return cached;
            using var stream = new FileStream(realPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (key is { } store)
            {
                if (Cache.Count >= MaxCached) Cache.Clear();
                Cache[store] = hash;
            }

            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // unreadable: no hash, so no listed hash matches and the run is refused
        }
    }
}
