using System.Text;

namespace Podium.Contracts;

/// <summary>
/// The length-prefixed encoding every signed or stored hash is computed over (grants, runs, scripts, access policies). Every field is
/// "present flag, byte length, bytes", so null and empty differ and no two splits give the same bytes. The web mirrors it byte for byte.
/// </summary>
public static class CanonicalBytes
{
    /// <summary>A string: 0 for null, else 1, the big-endian UTF-8 byte length and the UTF-8 bytes.</summary>
    public static void Put(Stream stream, string? value)
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

    /// <summary>A list of strings: 0 for null, else 1, the big-endian count and each string as <see cref="Put"/> writes it.</summary>
    public static void PutList(Stream stream, IReadOnlyList<string>? values)
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

    /// <summary>A 32-bit integer, big-endian.</summary>
    public static void PutInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
