using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// A person who leaves a workspace, or is removed from it, takes their Atlassian sign-ins on its connections with them, as when the account is
/// deleted: a connection they alone read with needs a new sign-in, and their link to a Jira person goes. Other workspaces are not touched.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationMemberRemovalTests(ApiFactory api)
{
    private static async Task<string> AccountUrlAsync(TestUser user, JiraWorld w)
    {
        var response = await user.PostAsync($"{w.Base}/account/connect");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
    }

    [Fact]
    public async Task Removing_a_member_or_the_connector_removes_their_sign_ins_and_links_and_the_connector_s_connection_needs_a_new_sign_in()
    {
        var owner = await api.NewClient().SignedUpAsync("imr-owner");
        var connector = await RemoteKit.MemberAsync(api, owner, "imr-connector", "admin");
        var member = await RemoteKit.MemberAsync(api, owner, "imr-member");
        var site = api.Jira.Site();
        site.Space("IMR").People.AddRange([new FakePerson("p-connector", "Connector Person", connector.Email), new FakePerson("p-member", "Member Person", member.Email)]);
        var connectorAccount = api.Jira.Account("Connector", connector.Email, site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(connector, api.Jira, connectorAccount, await JiraKit.StartAsync(connector, site.Subdomain, owner.WorkspaceId))));
        var id = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/integrations")).GetProperty("connections")[0].GetProperty("id").GetGuid();
        var world = new JiraWorld(api, owner, site, connectorAccount, id);
        (await connector.PostAsync($"{world.Base}/spaces/import", new { keys = new[] { "IMR" } })).EnsureSuccessStatusCode();
        var own = api.Jira.Account("Member", member.Email, site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(member, api.Jira, own, await AccountUrlAsync(member, world))));
        // The same person's sign-in in another workspace stays.
        var elsewhere = await JiraKit.ConnectedAsync(api, tag: "imr-elsewhere");
        var otherOwn = api.Jira.Account("Member Elsewhere", member.Email, elsewhere.Site);
        var invite = await elsewhere.Admin.PostAsync($"/api/workspaces/{elsewhere.WorkspaceId}/invitations", new { email = member.Email, role = "member" });
        invite.EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(member, api.Jira, otherOwn, await AccountUrlAsync(member, elsewhere))));

        await SessionKit.RemoveMemberAsync(owner, member);

        await using var db = api.Db();
        Assert.Equal([connector.Id], await db.IntegrationAccounts.Where(a => a.ConnectionId == id).Select(a => a.UserId).ToListAsync());
        Assert.Null((await db.IntegrationPeople.SingleAsync(p => p.ConnectionId == id && p.ExternalAccountId == "p-member")).UserId);
        Assert.Equal(connector.Id, (await db.IntegrationPeople.SingleAsync(p => p.ConnectionId == id && p.ExternalAccountId == "p-connector")).UserId);
        Assert.Equal(ConnectionStatuses.Active, (await JiraKit.ConnectionAsync(world)).Status);
        Assert.True(await db.IntegrationAccounts.AnyAsync(a => a.ConnectionId == elsewhere.ConnectionId && a.UserId == member.Id));

        // The connector leaves: their tokens no longer read, sync or invite for the workspace.
        Assert.Equal(HttpStatusCode.NoContent, (await connector.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{connector.Id}")).StatusCode);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.IntegrationAccounts.Where(a => a.ConnectionId == id).ToListAsync());
        Assert.Equal(ConnectionStatuses.Reauthorize, (await db.IntegrationConnections.AsNoTracking().SingleAsync(c => c.Id == id)).Status);
        Assert.Null((await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == id && p.ExternalAccountId == "p-connector")).UserId);
        var import = await owner.PostAsync($"{world.Base}/spaces/import", new { keys = new[] { "IMR" } });
        Assert.Equal("reauthorize", (await import.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single().GetProperty("outcome").GetString());
    }
}
