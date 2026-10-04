using System.Security.Cryptography;
using System.Text;

namespace ClaudeMonitor.Api.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238 over RFC 4226): HMAC-SHA1, 30-second steps, 6 digits, one step of clock
/// drift either way. What authenticator apps expect. A step once accepted is not accepted again (replay).
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8) counter[i] = (byte)(step & 0xff);
        // Triage (global #19): RFC 6238 and every authenticator app use HMAC-SHA1. HMAC does not depend on SHA-1's
        // collision resistance, so this is not the weakness CA5350 warns about.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The accepted step, or null: a 6-digit code within one step of now and later than the last one used.</summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long? lastStep)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != Digits) return null;
        var current = Step(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step > (lastStep ?? long.MinValue) && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
            {
                return step;
            }
        }

        return null;
    }

    public static string Base32(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>The address authenticator apps read (shown as text and as a link: no QR library is needed).</summary>
    public static string Uri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>Encrypts a small secret for storage with AES-256-GCM under the configured key (nonce | tag | ciphertext).</summary>
public static class SecretBox
{
    public static string Seal(byte[] key, byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static byte[] Open(byte[] key, string sealedText)
    {
        var data = Convert.FromBase64String(sealedText);
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }
}
