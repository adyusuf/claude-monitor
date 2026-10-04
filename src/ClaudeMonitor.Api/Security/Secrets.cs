using System.Security.Cryptography;
using System.Text;
using ClaudeMonitor.Api.Data;
using Microsoft.AspNetCore.Identity;

namespace ClaudeMonitor.Api.Security;

/// <summary>Random tokens, their hashes and passwords. Tokens are stored only as SHA-256 hashes.</summary>
public static class Secrets
{
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";
    private static readonly PasswordHasher<User> Hasher = new();

    // A hash of a random password nobody knows, made once with the same hasher (and so the same work factor) as real
    // ones: an unknown address costs a sign-in exactly one verification, like a known one.
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(new User(), NewToken()));

    public const int PasswordMin = 10;
    public const int PasswordMax = 256;

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>"BCDF-GHJK": 20 consonants, no vowels (no words), no look-alikes; about 34 bits.</summary>
    public static string NewUserCode()
    {
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < 9; i++)
        {
            chars[i] = i == 4 ? '-' : UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>Upper-cases and restores the dash, so "bcdfghjk" and "BCDF GHJK" find the same code.</summary>
    public static string NormalizeUserCode(string? code)
    {
        var letters = new string((code ?? "").Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        return letters.Length == 8 ? letters[..4] + "-" + letters[4..] : letters;
    }

    public static bool PasswordAcceptable(string? password) =>
        password is { Length: >= PasswordMin and <= PasswordMax } && !string.IsNullOrWhiteSpace(password);

    public static string HashPassword(User user, string password) => Hasher.HashPassword(user, password);

    public static bool VerifyPassword(User user, string password) =>
        user.PasswordHash is { } hash &&
        Hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;

    /// <summary>
    /// Always exactly one password verification: against the user's hash, or against a dummy hash when there is no
    /// user or the user has no password (provider-only, deleted). Timing then does not tell registered addresses apart.
    /// </summary>
    public static bool VerifyPasswordOrDummy(User? user, string? password)
    {
        if (user?.PasswordHash is not { } hash)
        {
            Hasher.VerifyHashedPassword(new User(), DummyHash.Value, password ?? "");
            return false;
        }

        return Hasher.VerifyHashedPassword(user, hash, password ?? "") != PasswordVerificationResult.Failed;
    }

    /// <summary>A stable, non-reversible tag for an IP address in the audit log (no raw address is stored).</summary>
    public static string? IpTag(System.Net.IPAddress? ip) => ip is null ? null : Hash("ip:" + ip)[..16];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
