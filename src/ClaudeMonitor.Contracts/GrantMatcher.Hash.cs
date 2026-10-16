using System.Security.Cryptography;
using System.Text;

namespace ClaudeMonitor.Contracts;

/// <summary>The hashes an approval binds to: a grant's template and a run's command, length-prefixed so no two splits collide.</summary>
public static partial class GrantMatcher
{
    /// <summary>Lowercase hex SHA-256 of a length-prefixed encoding of argv, cwd and longest timeout.</summary>
    public static string Hash(GrantTemplate grant)
    {
        using var buffer = new MemoryStream();
        Put(buffer, GrantHashTag);
        PutList(buffer, grant.Argv);
        Put(buffer, grant.Cwd);
        PutInt(buffer, grant.MaxTimeoutSeconds);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Lowercase hex SHA-256 of a length-prefixed encoding of a run; the owner's approval binds to it.</summary>
    public static string RunHash(string mode, IReadOnlyList<string>? argv, string? shellCommand, string? cwd, int timeoutSeconds,
        Guid targetAgentId)
    {
        using var buffer = new MemoryStream();
        Put(buffer, RunHashTag);
        Put(buffer, mode);
        Put(buffer, targetAgentId.ToString("D"));
        PutList(buffer, argv);
        Put(buffer, shellCommand);
        Put(buffer, cwd);
        PutInt(buffer, timeoutSeconds);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    // Every field is "present flag, byte length, bytes", so null and empty differ and no two splits give the same bytes.
    private static void Put(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        var bytes = Encoding.UTF8.GetBytes(value);
        PutInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void PutList(Stream stream, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        PutInt(stream, values.Count);
        foreach (var value in values)
        {
            Put(stream, value);
        }
    }

    private static void PutInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
