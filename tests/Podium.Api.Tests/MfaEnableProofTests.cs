using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>
/// F-028 (option A): turning two-step sign-in on needs the account password in the same request, or a fresh sign-in for an account without
/// one, so a stolen session cookie alone cannot lock the owner out. A password reset leaves two-step sign-in on.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MfaEnableProofTests(ApiFactory api)
{
    private static async Task<byte[]> SetupAsync(TestUser user)
    {
        var setup = await (await user.PostAsync("/api/me/mfa/setup")).Content.ReadFromJsonAsync<JsonElement>();
        return RemoteKit.FromBase32(setup.GetProperty("secret").GetString()!);
    }

    private string Code(byte[] secret) => RemoteKit.TotpNow(api, secret, -1);

    private static async Task<bool> EnabledAsync(TestUser user) => (await user.GetJsonAsync("/api/me")).GetProperty("mfaEnabled").GetBoolean();

    private static async Task<string?> ErrorAsync(HttpResponseMessage response, string field) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty(field)[0].GetString();

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    /// <summary>A GitHub-only account (no password), signed in now through the provider.</summary>
    private async Task<TestUser> ProviderOnlyAsync(string tag)
    {
        var client = api.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=signin");
        request.Headers.Add(TestExternal.Header, $"gh-{Guid.NewGuid()}|{Emails.New(tag)}|true|Provider Person");
        Assert.Equal(HttpStatusCode.Redirect, (await client.Http.SendAsync(request)).StatusCode);
        Assert.False((await client.GetJsonAsync("/api/me")).GetProperty("hasPassword").GetBoolean());
        return client;
    }

    [Fact]
    public async Task The_session_cookie_alone_is_refused_with_password_required()
    {
        var user = await api.NewClient().SignedUpAsync("mfa-nopass");
        var secret = await SetupAsync(user);

        var response = await user.PostAsync("/api/me/mfa/enable", new { code = Code(secret) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Podium.Api.Endpoints.MfaEndpoints.PasswordRequired, await ErrorAsync(response, "password"));
        Assert.False(await EnabledAsync(user));
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_counts_towards_the_lock()
    {
        var user = await api.NewClient().SignedUpAsync("mfa-wrongpass");
        var secret = await SetupAsync(user);

        var response = await user.PostAsync("/api/me/mfa/enable", new { code = Code(secret), password = "not the password!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_credentials", await ErrorAsync(response, "password"));
        Assert.False(await EnabledAsync(user));
        await using var db = api.Db();
        Assert.Contains(db.AuditEvents.Where(a => a.ActorUserId == user.Id && a.Action == AuditActions.SignInFailed).AsEnumerable(),
            a => a.Detail is not null && a.Detail.RootElement.GetProperty("step").GetString() == Podium.Api.Endpoints.MfaEndpoints.StepUpPasswordStep);
    }

    [Fact]
    public async Task The_right_password_with_the_code_turns_it_on()
    {
        var user = await api.NewClient().SignedUpAsync("mfa-rightpass");
        var secret = await SetupAsync(user);

        var response = await user.PostAsync("/api/me/mfa/enable", new { code = Code(secret), password = TestUser.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await EnabledAsync(user));
    }

    [Fact]
    public async Task An_account_without_a_password_turns_it_on_after_a_recent_sign_in()
    {
        var user = await ProviderOnlyAsync("mfa-provider-fresh");
        var secret = await SetupAsync(user);

        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync("/api/me/mfa/enable", new { code = Code(secret) })).StatusCode);
        Assert.True(await EnabledAsync(user));
    }

    [Fact]
    public async Task An_account_without_a_password_is_refused_after_an_old_sign_in()
    {
        var user = await ProviderOnlyAsync("mfa-provider-old");
        var secret = await SetupAsync(user);
        api.Clock.Advance(api.Services.GetRequiredService<ApiConfig>().ReauthWindow + TimeSpan.FromMinutes(1));

        var response = await user.PostAsync("/api/me/mfa/enable", new { code = Code(secret) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("reauth_required", await TitleAsync(response));
        Assert.False(await EnabledAsync(user));
    }

    [Fact]
    public async Task A_password_reset_leaves_two_step_sign_in_on()
    {
        var user = await api.NewClient().SignedUpAsync("mfa-reset");
        await RemoteKit.EnableTotpAsync(api, user);
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/auth/password/reset", new { token = api.Mail.TokenFor(user.Email), password = "a brand new password" }))
            .EnsureSuccessStatusCode();

        var login = await client.PostAsync("/api/auth/login", new { email = user.Email, password = "a brand new password" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("mfa_required", await TitleAsync(login));
    }
}
