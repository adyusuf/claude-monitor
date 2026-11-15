using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Single use holds under concurrency (F-176): a reset or verification link, a recovery code, a pending two-step token and a TOTP step are
/// spent with a compare-and-set, so two requests that both read them unused cannot both succeed. Each test lets both requests read first
/// (RowLockHold), then lets them write together.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class SingleUseRaceTests(ApiFactory api)
{
    private async Task<Guid> TokenIdAsync(Guid userId, string purpose)
    {
        await using var db = api.Db();
        return await db.UserTokens.AsNoTracking().Where(t => t.UserId == userId && t.Purpose == purpose && t.UsedAt == null)
            .OrderByDescending(t => t.CreatedAt).Select(t => t.Id).FirstAsync();
    }

    private async Task<(TestUser User, byte[] Secret, List<string> Recovery)> TwoStepAsync(string tag)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        var setup = await (await user.PostAsync("/api/me/mfa/setup")).Content.ReadFromJsonAsync<JsonElement>();
        var secret = Base32(setup.GetProperty("secret").GetString()!);
        var enabled = await user.PostAsync("/api/me/mfa/enable", new { code = RemoteKit.TotpNow(api, secret, -1) });
        var recovery = (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        return (user, secret, recovery);
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits < 8) continue;
            bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
            bits -= 8;
        }

        return [.. bytes];
    }

    private static async Task<string> PendingAsync(TestUser client, string email)
    {
        var body = await (await client.PostAsync("/api/auth/login", new { email, password = TestUser.Password })).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("mfaToken").GetString()!;
    }

    private async Task<int> OpenSessionsAsync(Guid userId)
    {
        await using var db = api.Db();
        return await db.LoginSessions.CountAsync(s => s.UserId == userId && s.RevokedAt == null);
    }

    [Fact]
    public async Task Two_resets_with_one_link_at_the_same_moment_change_the_password_once()
    {
        var user = await api.NewClient().SignedUpAsync("race-reset");
        (await api.NewClient().PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var token = api.Mail.TokenFor(user.Email);
        var id = await TokenIdAsync(user.Id, TokenPurposes.ResetPassword);
        TestUser a = api.NewClient(), b = api.NewClient();

        var answers = await RowLockHold.RunAsync(api, $"SELECT 1 FROM user_tokens WHERE id = {id} FOR UPDATE",
            () => a.PostAsync("/api/auth/password/reset", new { token, password = "first new password" }),
            () => b.PostAsync("/api/auth/password/reset", new { token, password = "second new password" }));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_verifications_with_one_link_at_the_same_moment_succeed_once()
    {
        var email = Emails.New("race-verify");
        await api.NewClient().PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "V" });
        var token = api.Mail.TokenFor(email);
        var normalized = Podium.Api.Text.SearchText.Email(email);
        await using var db = api.Db();
        var userId = await db.Users.Where(u => u.EmailNormalized == normalized).Select(u => u.Id).SingleAsync();
        var id = await TokenIdAsync(userId, TokenPurposes.VerifyEmail);
        TestUser a = api.NewClient(), b = api.NewClient();

        var answers = await RowLockHold.RunAsync(api, $"SELECT 1 FROM user_tokens WHERE id = {id} FOR UPDATE",
            () => a.PostAsync("/api/auth/verify-email", new { token }),
            () => b.PostAsync("/api/auth/verify-email", new { token }));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task One_recovery_code_used_by_two_sign_ins_at_the_same_moment_signs_in_once()
    {
        var (user, _, recovery) = await TwoStepAsync("race-recovery");
        TestUser a = api.NewClient(), b = api.NewClient();
        var (tokenA, tokenB) = (await PendingAsync(a, user.Email), await PendingAsync(b, user.Email));
        var before = await OpenSessionsAsync(user.Id);

        var answers = await RowLockHold.RunAsync(api, $"SELECT 1 FROM user_recovery_codes WHERE user_id = {user.Id} FOR UPDATE",
            () => a.PostAsync("/api/auth/mfa", new { token = tokenA, code = recovery[0] }),
            () => b.PostAsync("/api/auth/mfa", new { token = tokenB, code = recovery[0] }));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(before + 1, await OpenSessionsAsync(user.Id));
    }

    [Fact]
    public async Task One_totp_code_used_by_two_sign_ins_at_the_same_moment_signs_in_once()
    {
        var (user, secret, _) = await TwoStepAsync("race-totp");
        TestUser a = api.NewClient(), b = api.NewClient();
        var (tokenA, tokenB) = (await PendingAsync(a, user.Email), await PendingAsync(b, user.Email));
        var code = RemoteKit.TotpNow(api, secret);
        var before = await OpenSessionsAsync(user.Id);

        var answers = await RowLockHold.RunAsync(api, $"SELECT 1 FROM users WHERE id = {user.Id} FOR UPDATE",
            () => a.PostAsync("/api/auth/mfa", new { token = tokenA, code }),
            () => b.PostAsync("/api/auth/mfa", new { token = tokenB, code }));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(before + 1, await OpenSessionsAsync(user.Id));
    }

    [Fact]
    public async Task One_pending_token_used_by_two_codes_at_the_same_moment_opens_one_session()
    {
        var (user, _, recovery) = await TwoStepAsync("race-pending");
        var client = api.NewClient();
        var token = await PendingAsync(client, user.Email);
        var id = await TokenIdAsync(user.Id, TokenPurposes.MfaPending);
        TestUser a = api.NewClient(), b = api.NewClient();
        var before = await OpenSessionsAsync(user.Id);

        var answers = await RowLockHold.RunAsync(api, $"SELECT 1 FROM user_tokens WHERE id = {id} FOR UPDATE",
            () => a.PostAsync("/api/auth/mfa", new { token, code = recovery[0] }),
            () => b.PostAsync("/api/auth/mfa", new { token, code = recovery[1] }));

        Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal("invalid_token", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("token")[0].GetString());
        Assert.Equal(before + 1, await OpenSessionsAsync(user.Id));
    }
}
