using System.Security.Cryptography;
using System.Text;

namespace Podium.Contracts;

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

    /// <summary>
    /// The hash a script run's approval binds to: the interpreter, the exact script text, the arguments, the timeout, the target and the target's
    /// role as the card showed it. The argv and shell hash above does not change.
    /// </summary>
    public static string ScriptHash(string interpreter, string body, IReadOnlyList<string>? args, int timeoutSeconds, Guid targetAgentId,
        string? targetEnvironment, string? targetFunction, IReadOnlyList<ScriptFile>? files = null, string? stdin = null)
    {
        using var buffer = new MemoryStream();
        var extra = ScriptFiles.Any(files, stdin);
        Put(buffer, extra ? ScriptHashTagWithFiles : ScriptHashTag);
        Put(buffer, targetAgentId.ToString("D"));
        Put(buffer, interpreter);
        Put(buffer, body);
        PutList(buffer, args);
        PutInt(buffer, timeoutSeconds);
        Put(buffer, targetEnvironment);
        Put(buffer, targetFunction);
        if (extra)
        {
            // The files by path, so the order the requester sent them in does not change what the owner approved; the stdin as it is.
            var ordered = (files ?? []).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            PutInt(buffer, ordered.Count);
            foreach (var file in ordered)
            {
                Put(buffer, file.Path);
                Put(buffer, file.Text);
            }

            Put(buffer, stdin);
        }

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
