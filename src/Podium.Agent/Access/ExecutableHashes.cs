using System.Security.Cryptography;

namespace Podium.Agent.Access;

/// <summary>
/// The SHA-256 of an executable an access layer lists with a hash (ADR-0020 §4), read from the file every time it is asked for: a cache keyed on the
/// file's identity, size and modification time missed a file swapped in place for one of the same size with its time put back (the change time is not
/// readable the same way on every OS here). Null when the file cannot be read: the hash then matches nothing and the run is refused.
/// </summary>
internal static class ExecutableHashes
{
    public static string? Of(string realPath)
    {
        try
        {
            if (!File.Exists(realPath)) return null;
            using var stream = new FileStream(realPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // unreadable: no hash, so no listed hash matches and the run is refused
        }
    }
}
