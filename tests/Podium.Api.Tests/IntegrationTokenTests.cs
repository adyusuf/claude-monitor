using System.Net.Http.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>The vault of Atlassian tokens (ADR-0015 §5): a valid token is reused, a refresh token rotates once under a lock, and a refusal means sign in again.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationTokenTests(ApiFactory api)
{
    private ApiConfig Config => api.Services.GetRequiredService<ApiConfig>();

    private async Task<IntegrationAccount> AccountAsync(JiraWorld w, Guid? user = null)
    {
        await using var db = api.Db();
        return await db.IntegrationAccounts.AsNoTracking().SingleAsync(a => a.ConnectionId == w.ConnectionId && a.UserId == (user ?? w.Admin.Id));
    }

    private async Task ExpireInAsync(JiraWorld w, TimeSpan left)
    {
        await using var db = api.Db();
        var at = api.Clock.GetUtcNow() + left;
        await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId).ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessExpiresAt, at));
    }

    private async Task<string> AccessAsync(Guid account)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IntegrationTokens>().AccessAsync(account, CancellationToken.None);
    }

    private static int RefreshesWith(FakeJira jira, string refreshToken) =>
        jira.Requests.Count(r => r.Path == "/oauth/token" && r.Body.Contains($"\"refresh_token\":\"{refreshToken}\"", StringComparison.Ordinal));

    [Fact]
    public async Task A_token_that_is_still_valid_is_handed_out_without_asking_atlassian()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var account = await AccountAsync(w);
        var requests = api.Jira.Requests.Count;

        var token = await AccessAsync(account.Id);

        Assert.Equal(IntegrationTokens.Open(Config, account.AccessToken), token);
        Assert.Equal(requests, api.Jira.Requests.Count);
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_and_the_new_refresh_token_replaces_the_old_one_sealed()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var before = await AccountAsync(w);
        var oldRefresh = IntegrationTokens.Open(Config, before.RefreshToken);
        await ExpireInAsync(w, TimeSpan.FromMinutes(-1));

        var token = await AccessAsync(before.Id);

        var after = await AccountAsync(w);
        Assert.NotEqual(IntegrationTokens.Open(Config, before.AccessToken), token);
        Assert.Equal(token, IntegrationTokens.Open(Config, after.AccessToken));
        Assert.Equal(w.Account.CurrentRefreshToken, IntegrationTokens.Open(Config, after.RefreshToken));
        Assert.NotEqual(oldRefresh, w.Account.CurrentRefreshToken); // rotation: the old one is spent
        Assert.Equal(1, RefreshesWith(api.Jira, oldRefresh));
        Assert.True(after.AccessExpiresAt > api.Clock.GetUtcNow().AddMinutes(30));
        Assert.DoesNotContain("rt-", after.RefreshToken, StringComparison.Ordinal);
        Assert.Equal(AccountStatuses.Active, after.Status);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(59, true)]
    [InlineData(120, false)]
    public async Task A_token_that_expires_within_a_minute_is_refreshed_before_it_is_used(int secondsLeft, bool refreshed)
    {
        var w = await JiraKit.ConnectedAsync(api);
        var account = await AccountAsync(w);
        var refresh = IntegrationTokens.Open(Config, account.RefreshToken);
        await ExpireInAsync(w, TimeSpan.FromSeconds(secondsLeft));

        await AccessAsync(account.Id);

        Assert.Equal(refreshed ? 1 : 0, RefreshesWith(api.Jira, refresh));
    }

    [Fact]
    public async Task Two_callers_at_once_cause_exactly_one_refresh_and_get_the_same_token()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var account = await AccountAsync(w);
        var oldRefresh = IntegrationTokens.Open(Config, account.RefreshToken);
        await ExpireInAsync(w, TimeSpan.FromMinutes(-1));
        api.Jira.TokenDelay = TimeSpan.FromMilliseconds(300); // Atlassian takes its time, so the second caller arrives while the first waits
        try
        {
            var calls = await Task.WhenAll(AccessAsync(account.Id), AccessAsync(account.Id), AccessAsync(account.Id));

            Assert.Single(calls.Distinct());
            Assert.Equal(1, RefreshesWith(api.Jira, oldRefresh)); // a second refresh with the spent token would have burnt the new one
            Assert.Equal(AccountStatuses.Active, (await AccountAsync(w)).Status);
            Assert.Equal(w.Account.CurrentRefreshToken, IntegrationTokens.Open(Config, (await AccountAsync(w)).RefreshToken));
        }
        finally
        {
            api.Jira.TokenDelay = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task A_refused_refresh_of_the_connector_marks_the_account_and_the_connection_and_a_members_marks_only_their_account()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "tok-member");
        var own = api.Jira.Account("Member", member.Email, w.Site);
        var url = (await (await member.PostAsync($"{w.Base}/account/connect")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("authorizeUrl").GetString()!;
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(member, api.Jira, own, url)));
        await ExpireInAsync(w, TimeSpan.FromMinutes(-1));
        own.Revoked = true;

        var memberAccount = await AccountAsync(w, member.Id);
        var refused = await Assert.ThrowsAsync<TrackerException>(() => AccessAsync(memberAccount.Id));

        Assert.Equal(TrackerException.Codes.Unauthorized, refused.Code);
        Assert.Equal(AccountStatuses.Reauthorize, (await AccountAsync(w, member.Id)).Status);
        Assert.Equal(AccountStatuses.Active, (await AccountAsync(w)).Status);
        Assert.Equal(ConnectionStatuses.Active, (await JiraKit.ConnectionAsync(w)).Status); // the connector still reads for everybody
        // A dead account gives out nothing more, without another call to Atlassian.
        var requests = api.Jira.Requests.Count;
        await Assert.ThrowsAsync<TrackerException>(() => AccessAsync(memberAccount.Id));
        Assert.Equal(requests, api.Jira.Requests.Count);

        w.Account.Revoked = true;
        var connectorAccount = (await AccountAsync(w)).Id;
        await Assert.ThrowsAsync<TrackerException>(() => AccessAsync(connectorAccount));
        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal((ConnectionStatuses.Reauthorize, "tracker_unauthorized"), (connection.Status, connection.LastError));
    }

    [Fact]
    public async Task An_account_that_does_not_exist_gives_nothing()
    {
        var e = await Assert.ThrowsAsync<TrackerException>(() => AccessAsync(Guid.NewGuid()));

        Assert.Equal(TrackerException.Codes.Unauthorized, e.Code);
    }

    [Fact]
    public async Task A_context_exists_only_for_a_person_with_an_active_account_and_the_connector_context_only_while_the_connection_is_active()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "tok-ctx");
        using var scope = api.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IntegrationTokens>();
        var connection = await JiraKit.ConnectionAsync(w);

        var mine = await tokens.ContextForAsync(connection, w.Admin.Id, CancellationToken.None);

        Assert.Equal(w.Site.CloudId, mine!.SiteId);
        Assert.Null(await tokens.ContextForAsync(connection, member.Id, CancellationToken.None));
        Assert.NotNull(await tokens.ConnectorContextAsync(connection, CancellationToken.None));
        Assert.StartsWith("at-", await mine.AccessToken(CancellationToken.None), StringComparison.Ordinal);
        connection.Status = ConnectionStatuses.Reauthorize;
        Assert.Null(await tokens.ConnectorContextAsync(connection, CancellationToken.None));
        connection.Status = ConnectionStatuses.Disconnected;
        Assert.Null(await tokens.ConnectorContextAsync(connection, CancellationToken.None));
    }

    [Fact]
    public void Sealed_text_opens_only_with_the_integration_key_and_is_different_each_time()
    {
        var one = IntegrationTokens.Seal(Config, "secret-token");
        var two = IntegrationTokens.Seal(Config, "secret-token");

        Assert.NotEqual(one, two);
        Assert.DoesNotContain("secret-token", one, StringComparison.Ordinal);
        Assert.Equal("secret-token", IntegrationTokens.Open(Config, one));
        var otherKey = Config with { IntegrationKey = new byte[32] };
        Assert.ThrowsAny<Exception>(() => IntegrationTokens.Open(otherKey, one));
    }
}
