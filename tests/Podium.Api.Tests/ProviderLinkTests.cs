using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Config;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-181: linking or unlinking GitHub or Google needs a fresh sign-in (a session cookie alone attaches or removes no way in); the last way to
/// sign in cannot be unlinked; a password reset leaves the provider links.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ProviderLinkTests(ApiFactory api)
{
    private static string Header(string subject, string email = "") => $"{subject}|{email}|{(email.Length > 0 ? "true" : "false")}|Provider Person";

    private static async Task<HttpResponseMessage> DoneAsync(TestUser client, string provider, string header, string mode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/external/{provider}/done?mode={mode}");
        request.Headers.Add(TestExternal.Header, header);
        return await client.Http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> UnlinkAsync(TestUser user, string provider) => user.SendAsync(HttpMethod.Delete, $"/api/me/providers/{provider}");

    private void PastTheWindow() => api.Clock.Advance(api.Services.GetRequiredService<ApiConfig>().ReauthWindow + TimeSpan.FromMinutes(1));

    private async Task<string[]> ProvidersAsync(Guid userId)
    {
        await using var db = api.Db();
        return await db.UserLogins.Where(l => l.UserId == userId).Select(l => l.Provider).OrderBy(p => p).ToArrayAsync();
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    /// <summary>A GitHub-only account (no password), signed in now; its id.</summary>
    private async Task<(TestUser User, Guid Id)> ProviderOnlyAsync(string tag)
    {
        var client = api.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await DoneAsync(client, "github", Header("gh-" + Guid.NewGuid(), Emails.New(tag)), "signin")).StatusCode);
        return (client, (await client.GetJsonAsync("/api/me")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Linking_after_a_fresh_sign_in_links()
    {
        var user = await api.NewClient().SignedUpAsync("link-fresh");

        var done = await DoneAsync(user, "github", Header("gh-" + Guid.NewGuid()), "link");

        Assert.EndsWith("/settings?linked=github", done.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(["github"], await ProvidersAsync(user.Id));
    }

    [Fact]
    public async Task Linking_with_an_old_session_alone_is_refused_and_links_nothing()
    {
        var user = await api.NewClient().SignedUpAsync("link-old");
        PastTheWindow();

        var done = await DoneAsync(user, "github", Header("gh-" + Guid.NewGuid()), "link");

        Assert.EndsWith("/settings?error=" + ExternalAuthEndpoints.LinkReauthRequired, done.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Empty(await ProvidersAsync(user.Id));
    }

    [Fact]
    public async Task Unlinking_after_a_fresh_sign_in_with_a_password_left_removes_the_link()
    {
        var user = await api.NewClient().SignedUpAsync("unlink-fresh");
        await DoneAsync(user, "google", Header("g-" + Guid.NewGuid()), "link");

        Assert.Equal(HttpStatusCode.NoContent, (await UnlinkAsync(user, "google")).StatusCode);

        Assert.Empty(await ProvidersAsync(user.Id));
        await using var db = api.Db();
        Assert.True(await db.AuditEvents.AnyAsync(a => a.ActorUserId == user.Id && a.Action == Podium.Api.Security.AuditActions.ProviderUnlinked));
    }

    [Fact]
    public async Task Unlinking_with_an_old_session_alone_is_refused_and_keeps_the_link()
    {
        var user = await api.NewClient().SignedUpAsync("unlink-old");
        await DoneAsync(user, "google", Header("g-" + Guid.NewGuid()), "link");
        PastTheWindow();

        var response = await UnlinkAsync(user, "google");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("reauth_required", await TitleAsync(response));
        Assert.Equal(["google"], await ProvidersAsync(user.Id));
    }

    [Fact]
    public async Task The_last_way_to_sign_in_cannot_be_unlinked()
    {
        var (user, id) = await ProviderOnlyAsync("unlink-last");

        var response = await UnlinkAsync(user, "github");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ExternalAuthEndpoints.LastSignInMethod, await TitleAsync(response));
        Assert.Equal(["github"], await ProvidersAsync(id));
    }

    [Fact]
    public async Task A_provider_only_account_with_two_providers_may_unlink_one()
    {
        var (user, id) = await ProviderOnlyAsync("unlink-one-of-two");
        await DoneAsync(user, "google", Header("g-" + Guid.NewGuid()), "link");

        Assert.Equal(HttpStatusCode.NoContent, (await UnlinkAsync(user, "github")).StatusCode);

        Assert.Equal(["google"], await ProvidersAsync(id));
    }

    [Fact]
    public async Task Two_unlinks_at_once_never_leave_an_account_without_a_way_in()
    {
        var (user, id) = await ProviderOnlyAsync("unlink-race");
        await DoneAsync(user, "google", Header("g-" + Guid.NewGuid()), "link");

        var answers = await Task.WhenAll(UnlinkAsync(user, "github"), UnlinkAsync(user, "google"));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
        Assert.Single(await ProvidersAsync(id));
    }

    [Fact]
    public async Task Unlinking_a_provider_that_is_not_linked_or_not_known_is_404()
    {
        var user = await api.NewClient().SignedUpAsync("unlink-none");

        Assert.Equal(HttpStatusCode.NotFound, (await UnlinkAsync(user, "github")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UnlinkAsync(user, "myspace")).StatusCode);
    }

    [Fact]
    public async Task A_password_reset_leaves_the_provider_links()
    {
        var user = await api.NewClient().SignedUpAsync("link-reset");
        var subject = "gh-" + Guid.NewGuid();
        await DoneAsync(user, "github", Header(subject), "link");
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/auth/password/reset", new { token = api.Mail.TokenFor(user.Email), password = "a brand new password" }))
            .EnsureSuccessStatusCode();

        Assert.Equal(["github"], await ProvidersAsync(user.Id));
        var fresh = api.NewClient();
        Assert.Equal(ApiFactory.Origin + "/", (await DoneAsync(fresh, "github", Header(subject), "signin")).Headers.Location!.ToString());
        Assert.Equal(user.Id, (await fresh.GetJsonAsync("/api/me")).GetProperty("id").GetGuid());
    }
}
