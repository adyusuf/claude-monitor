using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>Connecting a Jira site with OAuth 2.0 (ADR-0015 §8): who may start, the round trip, what is stored, and the person's own account.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationConnectTests(ApiFactory api)
{
    private ApiConfig Config => api.Services.GetRequiredService<ApiConfig>();

    private static string Sub(FakeSite site) => site.Subdomain;

    [Fact]
    public async Task Only_a_workspace_admin_or_owner_can_start_a_connection_and_everybody_else_gets_a_404()
    {
        var owner = await api.NewClient().SignedUpAsync("ic-owner");
        var admin = await RemoteKit.MemberAsync(api, owner, "ic-admin", "admin");
        var member = await RemoteKit.MemberAsync(api, owner, "ic-member");
        var viewer = await RemoteKit.MemberAsync(api, owner, "ic-viewer", "viewer");
        var stranger = await api.NewClient().SignedUpAsync("ic-stranger");
        var site = api.Jira.Site();
        string Url(Guid ws) => $"/api/workspaces/{ws}/integrations/jira/connect";

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync(Url(owner.WorkspaceId), new { subdomain = Sub(site) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(Url(owner.WorkspaceId), new { subdomain = Sub(site) })).StatusCode);
        foreach (var other in new[] { member, viewer, stranger })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(Url(owner.WorkspaceId), new { subdomain = Sub(site) })).StatusCode);
        }
    }

    [Fact]
    public async Task Starting_needs_a_recent_sign_in()
    {
        var admin = await api.NewClient().SignedUpAsync("ic-reauth");
        var site = api.Jira.Site();
        await JiraKit.AgeSignInAsync(api, admin);

        await RemoteKit.AssertProblemAsync(await admin.PostAsync($"/api/workspaces/{admin.WorkspaceId}/integrations/jira/connect", new { subdomain = Sub(site) }),
            HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("evil.com#.atlassian.net")]
    [InlineData("http://acme.atlassian.net")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_site_that_is_not_an_atlassian_sub_domain_is_refused_before_anything_is_stored(string? subdomain)
    {
        var admin = await api.NewClient().SignedUpAsync("ic-invalid");

        var response = await admin.PostAsync($"/api/workspaces/{admin.WorkspaceId}/integrations/jira/connect", new { subdomain });

        Assert.Equal(("subdomain", "invalid_site"), await ProjectKit.ProblemAsync(response));
        await using var db = api.Db();
        Assert.Empty(await db.IntegrationOAuthStates.Where(s => s.UserId == admin.Id).ToListAsync());
    }

    [Fact]
    public async Task The_sign_in_address_is_atlassians_with_a_state_a_challenge_and_a_sealed_verifier_that_fits_it()
    {
        var admin = await api.NewClient().SignedUpAsync("ic-url");
        var site = api.Jira.Site();

        var url = await JiraKit.StartAsync(admin, "  " + Sub(site).ToUpperInvariant() + ".atlassian.net  ");

        Assert.StartsWith("https://auth.atlassian.com/authorize?", url, StringComparison.Ordinal);
        Assert.Equal(FakeJira.ClientId, JiraKit.Param(url, "client_id"));
        Assert.Equal("S256", JiraKit.Param(url, "code_challenge_method"));
        Assert.Equal($"{ApiFactory.Origin}/api/integrations/jira/callback", JiraKit.Param(url, "redirect_uri"));
        Assert.Contains("offline_access", JiraKit.Param(url, "scope"), StringComparison.Ordinal);
        await using var db = api.Db();
        var row = await db.IntegrationOAuthStates.AsNoTracking().SingleAsync(s => s.UserId == admin.Id);
        Assert.Equal((OAuthPurposes.Connect, site.Host, admin.WorkspaceId), (row.Purpose, row.SiteHost, row.WorkspaceId));
        Assert.NotEqual(JiraKit.Param(url, "state"), row.StateHash); // only the hash of the state is kept
        Assert.Equal(JiraKit.Param(url, "code_challenge"), JiraOAuth.Challenge(IntegrationTokens.Open(Config, row.Verifier)));
        Assert.True(row.ExpiresAt - row.CreatedAt <= Podium.Api.Endpoints.IntegrationEndpoints.StateLifetime);
    }

    [Fact]
    public async Task A_successful_round_trip_makes_one_connection_and_one_account_with_sealed_tokens_and_leaks_nothing()
    {
        var w = await JiraKit.ConnectedAsync(api);

        await using var db = api.Db();
        var connection = await db.IntegrationConnections.AsNoTracking().SingleAsync(c => c.WorkspaceId == w.WorkspaceId);
        var account = await db.IntegrationAccounts.AsNoTracking().SingleAsync(a => a.ConnectionId == connection.Id);
        Assert.Equal(("jira", w.Site.Host, w.Site.CloudId, w.Admin.Id, ConnectionStatuses.Active, false), (connection.Provider, connection.SiteHost, connection.CloudId, connection.ConnectorUserId, connection.Status, connection.AutoInvite));
        Assert.Equal((w.Admin.Id, w.Account.Id, "Connector", AccountStatuses.Active), (account.UserId, account.ExternalAccountId, account.DisplayName, account.Status));
        // Sealed: not the plain token, and it opens (with the integration key) to exactly what Atlassian issued.
        Assert.DoesNotContain("rt-", account.RefreshToken, StringComparison.Ordinal);
        Assert.DoesNotContain("at-", account.AccessToken, StringComparison.Ordinal);
        Assert.Equal(w.Account.CurrentRefreshToken, IntegrationTokens.Open(Config, account.RefreshToken));
        Assert.StartsWith("at-", IntegrationTokens.Open(Config, account.AccessToken), StringComparison.Ordinal);
        Assert.True(account.AccessExpiresAt > api.Clock.GetUtcNow());
        // The state was used up, and what the web is told holds no secret.
        Assert.Empty(await db.IntegrationOAuthStates.Where(s => s.UserId == w.Admin.Id).ToListAsync());
        await JiraKit.AssertNoSecretAnywhereAsync(w);
    }

    [Fact]
    public async Task The_list_shows_the_connection_with_what_is_mine_and_a_member_sees_it_too()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ic-list-member");

        var admin = (await w.Admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations"));
        var seen = (await member.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations"));

        Assert.True(admin.GetProperty("available").GetBoolean());
        Assert.Equal(["jira"], admin.GetProperty("trackerProviders").EnumerateArray().Select(p => p.GetString()));
        var mine = admin.GetProperty("connections")[0];
        Assert.Equal((w.ConnectionId, "jira", w.Site.Host, "active", false, true, true, "active", false, 0),
            (mine.GetProperty("id").GetGuid(), mine.GetProperty("provider").GetString(), mine.GetProperty("siteHost").GetString(), mine.GetProperty("status").GetString(),
                mine.GetProperty("autoInvite").GetBoolean(), mine.GetProperty("connectedByMe").GetBoolean(), mine.GetProperty("myAccountLinked").GetBoolean(),
                mine.GetProperty("myAccountStatus").GetString(), mine.GetProperty("webhookActive").GetBoolean(), mine.GetProperty("projectCount").GetInt32()));
        Assert.Equal("Test Person", mine.GetProperty("connectorName").GetString());
        var theirs = seen.GetProperty("connections")[0];
        Assert.Equal((w.ConnectionId, false, false, JsonValueKind.Null), (theirs.GetProperty("id").GetGuid(), theirs.GetProperty("connectedByMe").GetBoolean(),
            theirs.GetProperty("myAccountLinked").GetBoolean(), theirs.GetProperty("myAccountStatus").ValueKind));
    }

    [Fact]
    public async Task Connecting_the_same_site_again_reuses_the_connection_and_replaces_the_tokens()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var before = (await ConnectionRowsAsync(w)).Account.RefreshToken;

        var again = await JiraKit.ApproveAsync(w.Admin, api.Jira, w.Account, await JiraKit.StartAsync(w.Admin, w.Site.Subdomain));

        Assert.Equal("ok", JiraKit.ResultOf(again));
        var (connections, accounts, account) = await ConnectionRowsAsync(w);
        Assert.Equal((1, 1), (connections, accounts));
        Assert.NotEqual(before, account.RefreshToken);
        Assert.Equal(w.Account.CurrentRefreshToken, IntegrationTokens.Open(Config, account.RefreshToken));
    }

    [Fact]
    public async Task A_state_started_by_somebody_else_is_refused_and_stays_usable_for_its_owner()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var other = await RemoteKit.MemberAsync(api, w.Admin, "ic-thief", "admin");
        var site = api.Jira.Site();
        var thief = api.Jira.Account("Thief", other.Email, site);
        var url = await JiraKit.StartAsync(w.Admin, site.Subdomain);

        var stolen = await JiraKit.ApproveAsync(other, api.Jira, thief, url);

        Assert.Equal("invalid_state", JiraKit.ResultOf(stolen));
        Assert.Equal(1, await CountConnectionsAsync(w.WorkspaceId));
        var mine = api.Jira.Account("Mine", w.Admin.Email, site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(w.Admin, api.Jira, mine, url)));
        Assert.Equal(2, await CountConnectionsAsync(w.WorkspaceId));
    }

    [Fact]
    public async Task An_expired_state_and_a_state_used_twice_are_refused()
    {
        var admin = await api.NewClient().SignedUpAsync("ic-expiry");
        var site = api.Jira.Site();
        var account = api.Jira.Account("A", admin.Email, site);
        var stale = await JiraKit.StartAsync(admin, site.Subdomain);
        await using (var db = api.Db())
        {
            var past = api.Clock.GetUtcNow().AddSeconds(-1);
            await db.IntegrationOAuthStates.Where(s => s.UserId == admin.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, past));
        }

        Assert.Equal("invalid_state", JiraKit.ResultOf(await JiraKit.ApproveAsync(admin, api.Jira, account, stale)));

        var url = await JiraKit.StartAsync(admin, site.Subdomain);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(admin, api.Jira, account, url)));
        Assert.Equal("invalid_state", JiraKit.ResultOf(await JiraKit.ApproveAsync(admin, api.Jira, account, url)));
        Assert.Equal(1, await CountConnectionsAsync(admin.WorkspaceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-state")]
    public async Task A_missing_or_unknown_state_goes_back_to_the_start_page(string? state)
    {
        var admin = await api.NewClient().SignedUpAsync("ic-nostate");

        var response = await admin.Http.GetAsync("/api/integrations/jira/callback?code=abc" + (state is null ? "" : "&state=" + state));

        Assert.Equal("invalid_state", JiraKit.ResultOf(response));
        Assert.Equal($"{ApiFactory.Origin}/?result=invalid_state", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_person_who_says_no_at_atlassian_ends_in_denied_and_nothing_is_stored()
    {
        var admin = await api.NewClient().SignedUpAsync("ic-denied");
        var url = await JiraKit.StartAsync(admin, api.Jira.Site().Subdomain);

        var denied = await admin.Http.GetAsync($"/api/integrations/jira/callback?error=access_denied&state={Uri.EscapeDataString(JiraKit.Param(url, "state"))}");

        Assert.Equal("denied", JiraKit.ResultOf(denied));
        Assert.Equal($"{ApiFactory.Origin}/w/{admin.WorkspaceId}/integrations?result=denied", denied.Headers.Location!.ToString());
        await using var db = api.Db();
        Assert.Equal((0, 0), (await db.IntegrationOAuthStates.CountAsync(s => s.UserId == admin.Id), await CountConnectionsAsync(admin.WorkspaceId)));
    }

    [Fact]
    public async Task A_site_the_account_cannot_reach_ends_in_site_not_found_and_a_refused_code_in_unauthorized()
    {
        var admin = await api.NewClient().SignedUpAsync("ic-nosite");
        var wanted = api.Jira.Site();
        var elsewhere = api.Jira.Site();
        var account = api.Jira.Account("A", admin.Email, elsewhere); // has another site only

        var wrongSite = await JiraKit.ApproveAsync(admin, api.Jira, account, await JiraKit.StartAsync(admin, wanted.Subdomain));
        var badCode = await JiraKit.CallbackAsync(admin, "a-code-nobody-issued", JiraKit.Param(await JiraKit.StartAsync(admin, wanted.Subdomain), "state"));

        Assert.Equal(IntegrationConnect.SiteNotFound, JiraKit.ResultOf(wrongSite));
        Assert.Equal(TrackerException.Codes.Unauthorized, JiraKit.ResultOf(badCode));
        Assert.Equal(0, await CountConnectionsAsync(admin.WorkspaceId));
    }

    [Fact]
    public async Task An_admin_who_lost_the_right_before_the_callback_connects_nothing()
    {
        var owner = await api.NewClient().SignedUpAsync("ic-demote-owner");
        var admin = await RemoteKit.MemberAsync(api, owner, "ic-demote-admin", "admin");
        var site = api.Jira.Site();
        var account = api.Jira.Account("A", admin.Email, site);
        var url = await JiraKit.StartAsync(admin, site.Subdomain, owner.WorkspaceId);
        (await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{admin.Id}", new { role = "member" })).EnsureSuccessStatusCode();

        Assert.Equal("invalid_state", JiraKit.ResultOf(await JiraKit.ApproveAsync(admin, api.Jira, account, url)));
        Assert.Equal(0, await CountConnectionsAsync(owner.WorkspaceId));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static async Task<string> Account(TestUser user, JiraWorld w)
    {
        var response = await user.PostAsync($"{w.Base}/account/connect");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
    }

    private async Task<int> CountConnectionsAsync(Guid workspace)
    {
        await using var db = api.Db();
        return await db.IntegrationConnections.CountAsync(c => c.WorkspaceId == workspace);
    }

    private async Task<(int Connections, int Accounts, IntegrationAccount Account)> ConnectionRowsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        var connections = await db.IntegrationConnections.CountAsync(c => c.WorkspaceId == w.WorkspaceId);
        var accounts = await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId).ToListAsync();
        return (connections, accounts.Count, accounts.Single(a => a.UserId == w.Admin.Id));
    }
}
