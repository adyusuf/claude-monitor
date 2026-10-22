using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>A person's own Atlassian account on a connection (ADR-0015 §1 decision 3, §8): who may link one and what it changes.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationAccountTests(ApiFactory api)
{
    private static async Task<string> Account(TestUser user, JiraWorld w)
    {
        var response = await user.PostAsync($"{w.Base}/account/connect");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
    }

    [Fact]
    public async Task A_member_links_their_own_account_and_the_connector_stays_who_it_was()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ic-link-member");
        var own = api.Jira.Account("Member Own", member.Email, w.Site);
        var url = await Account(member, w);

        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(member, api.Jira, own, url)));

        await using var db = api.Db();
        var accounts = await db.IntegrationAccounts.AsNoTracking().Where(a => a.ConnectionId == w.ConnectionId).ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal(("Member Own", own.Id), accounts.Where(a => a.UserId == member.Id).Select(a => (a.DisplayName, a.ExternalAccountId)).Single());
        Assert.Equal(w.Admin.Id, (await JiraKit.ConnectionAsync(w)).ConnectorUserId);
        var mine = (await member.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections")[0];
        Assert.True(mine.GetProperty("myAccountLinked").GetBoolean());
        Assert.False(mine.GetProperty("connectedByMe").GetBoolean());
        await JiraKit.AssertNoSecretAnywhereAsync(w);
    }

    [Fact]
    public async Task Linking_an_account_needs_a_member_of_that_workspace_and_an_account_that_reaches_the_site()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "ic-link-viewer", "viewer");
        var stranger = await api.NewClient().SignedUpAsync("ic-link-stranger");
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ic-link-nosite");

        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync($"{w.Base}/account/connect")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"{w.Base}/account/connect")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.PostAsync($"/api/integrations/{Guid.NewGuid()}/account/connect")).StatusCode);
        var url = await Account(member, w);
        var noSite = api.Jira.Account("No Access", member.Email, api.Jira.Site());
        Assert.Equal(IntegrationConnect.SiteNotFound, JiraKit.ResultOf(await JiraKit.ApproveAsync(member, api.Jira, noSite, url)));
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_atlassian_sign_ins_and_links_and_a_connection_it_alone_read_with_needs_a_new_sign_in()
    {
        var owner = await api.NewClient().SignedUpAsync("acd-owner");
        var connector = await RemoteKit.MemberAsync(api, owner, "acd-connector", "admin");
        var member = await RemoteKit.MemberAsync(api, owner, "acd-member");
        var site = api.Jira.Site();
        site.Space("ACD").People.AddRange([new FakePerson("p-connector", "Connector Person", connector.Email), new FakePerson("p-member", "Member Person", member.Email)]);
        var connectorAccount = api.Jira.Account("Connector", connector.Email, site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(connector, api.Jira, connectorAccount, await JiraKit.StartAsync(connector, site.Subdomain, owner.WorkspaceId))));
        var id = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/integrations")).GetProperty("connections")[0].GetProperty("id").GetGuid();
        var world = new JiraWorld(api, owner, site, connectorAccount, id);
        (await connector.PostAsync($"{world.Base}/spaces/import", new { keys = new[] { "ACD" } })).EnsureSuccessStatusCode();
        var own = api.Jira.Account("Member", member.Email, site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(member, api.Jira, own, await Account(member, world))));

        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        await using var db = api.Db();
        Assert.Equal([connector.Id], await db.IntegrationAccounts.Where(a => a.ConnectionId == id).Select(a => a.UserId).ToListAsync());
        Assert.Null((await db.IntegrationPeople.SingleAsync(p => p.ConnectionId == id && p.ExternalAccountId == "p-member")).UserId); // the link to the person goes with the person
        Assert.Equal(connector.Id, (await db.IntegrationPeople.SingleAsync(p => p.ConnectionId == id && p.ExternalAccountId == "p-connector")).UserId);
        Assert.Equal(ConnectionStatuses.Active, (await JiraKit.ConnectionAsync(world)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await connector.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        Assert.Empty(await db.IntegrationAccounts.Where(a => a.ConnectionId == id).ToListAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(ConnectionStatuses.Reauthorize, (await db.IntegrationConnections.AsNoTracking().SingleAsync(c => c.Id == id)).Status);
        Assert.Null((await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == id && p.ExternalAccountId == "p-connector")).UserId);
        // Somebody else connecting again repairs it.
        var back = api.Jira.Account("Back", owner.Email, site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(owner, api.Jira, back, await JiraKit.StartAsync(owner, site.Subdomain))));
        Assert.Equal((ConnectionStatuses.Active, owner.Id), ((await JiraKit.ConnectionAsync(world)).Status, (await JiraKit.ConnectionAsync(world)).ConnectorUserId));
    }
}
