using System.Security.Cryptography;
using System.Text;
using Podium.Api.Integrations;

namespace Podium.Api.Tests;

/// <summary>The signed token of a Jira webhook delivery (ADR-0015 §6): nothing is trusted before the signature, and only HS256 is accepted.</summary>
public sealed class WebhookJwtTests
{
    private const string Secret = "the-app-client-secret";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64(string text) => B64(Encoding.UTF8.GetBytes(text));

    /// <summary>A token made by hand with any header and any signature algorithm.</summary>
    private static string Craft(string header, string payload, string secret, string algorithm = "HS256")
    {
        var signingInput = B64(header) + "." + B64(payload);
        var key = Encoding.UTF8.GetBytes(secret);
        var signature = algorithm switch
        {
            "HS512" => HMACSHA512.HashData(key, Encoding.ASCII.GetBytes(signingInput)),
            "none" => [],
            _ => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput)),
        };
        return signingInput + "." + B64(signature);
    }

    private static bool Check(string? header, string secret = Secret) => WebhookJwt.Verify(header, secret, Now);

    [Fact]
    public void A_token_signed_with_the_secret_is_accepted_with_or_without_an_expiry()
    {
        Assert.True(Check("Bearer " + WebhookJwt.Sign("""{"iss":"x"}""", Secret)));
        Assert.True(Check("Bearer " + WebhookJwt.Sign($$"""{"exp":{{Now.AddMinutes(10).ToUnixTimeSeconds()}}}""", Secret)));
        Assert.True(Check("Bearer " + WebhookJwt.Sign("""{"iss":"x"}""", Secret) + "  ")); // trailing space is trimmed
    }

    [Fact]
    public void A_token_signed_with_another_secret_is_refused()
    {
        Assert.False(Check("Bearer " + WebhookJwt.Sign("""{"iss":"x"}""", "another-secret")));
        Assert.False(Check("Bearer " + WebhookJwt.Sign("""{"iss":"x"}""", Secret), secret: "another-secret"));
    }

    [Theory]
    [InlineData("""{"alg":"none","typ":"JWT"}""", "none")]
    [InlineData("""{"alg":"HS512","typ":"JWT"}""", "HS512")]
    [InlineData("""{"alg":"RS256","typ":"JWT"}""", "HS256")]
    [InlineData("""{"typ":"JWT"}""", "HS256")]
    [InlineData("""[]""", "HS256")]
    public void Only_the_hs256_algorithm_is_accepted_even_when_the_signature_fits_the_header(string header, string algorithm) =>
        Assert.False(Check("Bearer " + Craft(header, """{"iss":"x"}""", Secret, algorithm)));

    [Fact]
    public void A_tampered_payload_or_header_breaks_the_signature()
    {
        var parts = WebhookJwt.Sign("""{"iss":"x"}""", Secret).Split('.');
        var evilPayload = B64("""{"iss":"y"}""");

        Assert.False(Check($"Bearer {parts[0]}.{evilPayload}.{parts[2]}"));
        Assert.False(Check($"Bearer {B64("""{"alg":"HS256"}""")}.{parts[1]}.{parts[2]}"));
        Assert.False(Check($"Bearer {parts[0]}.{parts[1]}.{B64("not the signature")}"));
    }

    [Fact]
    public void An_expired_token_is_refused_beyond_the_clock_skew()
    {
        string Token(TimeSpan fromNow) => "Bearer " + WebhookJwt.Sign($$"""{"exp":{{(Now + fromNow).ToUnixTimeSeconds()}}}""", Secret);

        Assert.False(Check(Token(-(WebhookJwt.Skew + TimeSpan.FromSeconds(1)))));
        Assert.True(Check(Token(-(WebhookJwt.Skew - TimeSpan.FromSeconds(1)))));
        Assert.True(Check(Token(TimeSpan.FromHours(1))));
        Assert.False(Check("Bearer " + WebhookJwt.Sign($$"""{"exp":{{long.MaxValue}}}""", Secret))); // out of range: refused, not a crash
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic abc.def.ghi")]
    [InlineData("bearer a.b.c")]
    [InlineData("Bearer a.b")]
    [InlineData("Bearer a.b.c.d")]
    [InlineData("Bearer ..")]
    [InlineData("Bearer a..c")]
    [InlineData("Bearer !!!.@@@.###")]
    [InlineData("Bearer e30.e30.e30")]
    public void A_missing_or_malformed_header_is_refused(string? header) => Assert.False(Check(header));

    [Fact]
    public void A_payload_that_is_not_a_json_object_is_refused_even_when_signed()
    {
        Assert.False(Check("Bearer " + Craft("""{"alg":"HS256"}""", "[1,2]", Secret)));
        Assert.False(Check("Bearer " + Craft("""{"alg":"HS256"}""", "not json", Secret)));
    }
}
