using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// The second step's lock counts across password sign-ins (F-026): a correct password alone resets nothing for an account with two-step
/// sign-in on, so "sign in, guess, sign in again" reaches the lock like guessing on one token does.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MfaRelockTests(ApiFactory api)
{
    private static async Task<string> MfaTokenAsync(TestUser client, string email)
    {
        var response = await client.PostAsync("/api/auth/login", new { email, password = TestUser.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mfa_required", body.GetProperty("title").GetString());
        return body.GetProperty("mfaToken").GetString()!;
    }

    [Fact]
    public async Task Signing_in_again_between_wrong_codes_still_locks_the_account_at_the_limit()
    {
        var user = await api.NewClient().SignedUpAsync("mfa-relock");
        var secret = await RemoteKit.EnableTotpAsync(api, user);
        var client = api.NewClient();
        for (var i = 0; i < 4; i++)
        {
            var token = await MfaTokenAsync(client, user.Email);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa", new { token, code = "00000" + i })).StatusCode);
        }

        var spare = await MfaTokenAsync(client, user.Email);
        var last = await MfaTokenAsync(client, user.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa", new { token = last, code = "000009" })).StatusCode);

        var login = await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("invalid_credentials", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        var locked = await client.PostAsync("/api/auth/mfa", new { token = spare, code = RemoteKit.TotpNow(api, secret) });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("account_locked", (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_finished_second_step_starts_the_count_again()
    {
        var user = await api.NewClient().SignedUpAsync("mfa-relock-reset");
        var secret = await RemoteKit.EnableTotpAsync(api, user);
        var client = api.NewClient();
        for (var i = 0; i < 4; i++)
        {
            var token = await MfaTokenAsync(client, user.Email);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/mfa", new { token, code = "00000" + i })).StatusCode);
        }

        var good = await MfaTokenAsync(client, user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/mfa", new { token = good, code = RemoteKit.TotpNow(api, secret) })).StatusCode);
        var after = api.NewClient();
        var next = await MfaTokenAsync(after, user.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await after.PostAsync("/api/auth/mfa", new { token = next, code = "000008" })).StatusCode);
        Assert.Equal("mfa_required", (await (await after.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }
}
