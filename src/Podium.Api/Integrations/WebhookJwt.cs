using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Podium.Api.Integrations;

/// <summary>
/// Checks the <c>Authorization: Bearer &lt;JWT&gt;</c> of a Jira webhook delivery to an OAuth app: HS256, signed with the app's client secret
/// (ADR-0015 §6). Nothing in the token is trusted before the signature matches; <c>exp</c> is checked when it is present. Unverified against a
/// real site: the exact claims Atlassian sends are not known here, so only the signature and the expiry are required.
/// </summary>
public static class WebhookJwt
{
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public static bool Verify(string? authorization, string secret, DateTimeOffset now)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = authorization[prefix.Length..].Trim().Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0)) return false;
        var header = Decode(parts[0]);
        var payload = Decode(parts[1]);
        var signature = Decode(parts[2]);
        if (header is null || payload is null || signature is null) return false;
        try
        {
            using var h = JsonDocument.Parse(header);
            if (h.RootElement.ValueKind != JsonValueKind.Object || !h.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256") return false;
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return false;
            using var p = JsonDocument.Parse(payload);
            return p.RootElement.ValueKind == JsonValueKind.Object
                   && !(p.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                        && DateTimeOffset.FromUnixTimeSeconds(seconds) + Skew < now);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>For tests and tools: a token signed the way the check expects.</summary>
    public static string Sign(string payloadJson, string secret)
    {
        var head = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = Encode(Encoding.UTF8.GetBytes(payloadJson));
        var sig = Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)));
        return $"{head}.{body}.{sig}";
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
