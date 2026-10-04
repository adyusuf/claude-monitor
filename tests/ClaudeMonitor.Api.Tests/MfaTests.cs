using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

public sealed class TotpUnitTests
{
    [Fact]
    public void Matches_the_rfc_6238_test_vector_and_base32()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, Totp.Step(DateTimeOffset.FromUnixTimeSeconds(59))));
        Assert.Equal("081804", Totp.Code(secret, Totp.Step(DateTimeOffset.FromUnixTimeSeconds(1111111109))));
        Assert.Equal("MZXW6YTBOI", Totp.Base32(Encoding.ASCII.GetBytes("foobar")));
        Assert.StartsWith("otpauth://totp/Claude%20Monitor:a%40b.co?secret=", Totp.Uri("Claude Monitor", "a@b.co", secret), StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_one_step_of_drift_and_never_the_same_step_twice()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.Step(now);
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Code(secret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(secret, Totp.Code(secret, step + 1), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step - 2), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step), now, step));
        Assert.Null(Totp.Verify(secret, "12345", now, null));
        Assert.Null(Totp.Verify(secret, null, now, null));
        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, step)[..3] + " " + Totp.Code(secret, step)[3..], now, null));
    }

    [Fact]
    public void Seals_and_opens_with_the_same_key_only()
    {
        var key = new byte[32];
        var sealedText = SecretBox.Seal(key, [1, 2, 3]);
        Assert.Equal([1, 2, 3], SecretBox.Open(key, sealedText));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => SecretBox.Open(new byte[32] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, sealedText));
    }
}

[Collection(ApiGroup.Name)]
public sealed class MfaTests(ApiFactory api)
{
    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    private string Now(byte[] secret, int stepOffset = 0) => Totp.Code(secret, Totp.Step(api.Clock.GetUtcNow()) + stepOffset);

    private async Task<(TestUser User, byte[] Secret, List<string> Recovery)> EnabledAsync(string tag)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        var setup = await (await user.PostAsync("/api/me/mfa/setup")).Content.ReadFromJsonAsync<JsonElement>();
        var secret = FromBase32(setup.GetProperty("secret").GetString()!);
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("uri").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/me/mfa/enable", new { code = "000000" })).StatusCode);
        var enabled = await user.PostAsync("/api/me/mfa/enable", new { code = Now(secret, -1) });
        var recovery = (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recovery.Count);
        Assert.True((await user.GetJsonAsync("/api/me")).GetProperty("mfaEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync("/api/me/mfa/setup")).StatusCode);
        return (user, secret, recovery);
    }

    private static async Task<string> MfaTokenAsync(TestUser client, string email)
    {
        var response = await client.PostAsync("/api/auth/login", new { email, password = TestUser.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mfa_required", body.GetProperty("title").GetString());
        return body.GetProperty("mfaToken").GetString()!;
    }

    [Fact]
    public async Task Sign_in_asks_for_a_code_and_a_used_code_does_not_work_again()
    {
        var (user, secret, _) = await EnabledAsync("mfa-signin");
        var client = api.NewClient();
        var token = await MfaTokenAsync(client, user.Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa", new { token, code = "111111" })).StatusCode);
        var code = Now(secret);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/mfa", new { token, code })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa", new { token, code })).StatusCode);

        var again = api.NewClient();
        var second = await MfaTokenAsync(again, user.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PostAsync("/api/auth/mfa", new { token = second, code })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PostAsync("/api/auth/mfa", new { token = "made-up", code })).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_the_pending_token_expires()
    {
        var (user, _, recovery) = await EnabledAsync("mfa-recovery");
        var client = api.NewClient();
        var token = await MfaTokenAsync(client, user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/mfa", new { token, code = recovery[0].ToUpperInvariant() })).StatusCode);
        var other = api.NewClient();
        var token2 = await MfaTokenAsync(other, user.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsync("/api/auth/mfa", new { token = token2, code = recovery[0] })).StatusCode);
        api.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsync("/api/auth/mfa", new { token = token2, code = recovery[1] })).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account()
    {
        var (user, secret, _) = await EnabledAsync("mfa-lock");
        var client = api.NewClient();
        var token = await MfaTokenAsync(client, user.Email);
        for (var i = 0; i < 5; i++) await client.PostAsync("/api/auth/mfa", new { token, code = "000001" });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa", new { token, code = Now(secret) })).StatusCode);
    }

    [Fact]
    public async Task A_provider_sign_in_also_takes_the_second_step()
    {
        var (user, _, _) = await EnabledAsync("mfa-provider");
        var subject = "gh-" + Guid.NewGuid();
        using (var link = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=link"))
        {
            link.Headers.Add(TestExternal.Header, $"{subject}||false|x");
            await user.Http.SendAsync(link);
        }

        var fresh = api.NewClient();
        using var signIn = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=signin");
        signIn.Headers.Add(TestExternal.Header, $"{subject}||false|x");
        var done = await fresh.Http.SendAsync(signIn);
        Assert.Contains("/mfa?token=", done.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_and_deleting_the_account_need_a_code()
    {
        var (user, secret, recovery) = await EnabledAsync("mfa-off");
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/me/mfa/disable", new { code = "999999" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/me/mfa/disable", new { code = Now(secret, 1) })).StatusCode);
        Assert.False((await user.GetJsonAsync("/api/me")).GetProperty("mfaEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync("/api/me/mfa/disable", new { code = recovery[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync("/api/me/mfa/enable", new { code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.NewClient().PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);

        var (other, otherSecret, _) = await EnabledAsync("mfa-delete");
        Assert.Equal(HttpStatusCode.NoContent,
            (await other.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE", code = Now(otherSecret) })).StatusCode);
    }
}
