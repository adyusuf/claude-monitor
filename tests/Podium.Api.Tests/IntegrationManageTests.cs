using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Changing and ending a connection (ADR-0015 §8): the switch for automatic invitations and disconnecting.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationManageTests(ApiFactory api)
{
    private async Task<int> CountConnectionsAsync(Guid workspace)
    {
        await using var db = api.Db();
        return await db.IntegrationConnections.CountAsync(c => c.WorkspaceId == workspace);
    }

    [Fact]
    public async Task Disconnecting_clears_the_accounts_keeps_the_imported_rows_and_lets_the_site_be_connected_again()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("DIS").Issue("Imported before"));
        await JiraKit.ImportAsync(w, "DIS");
        var project = await JiraKit.ProjectIdAsync(w, "DIS");
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ic-disc-member");
        var other = await JiraKit.ConnectedAsync(api);

        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Delete, w.Base)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.SendAsync(HttpMethod.Delete, w.Base)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.SendAsync(HttpMethod.Delete, w.Base)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.SendAsync(HttpMethod.Delete, w.Base)).StatusCode);

        await using var db = api.Db();
        Assert.Equal(ConnectionStatuses.Disconnected, (await JiraKit.ConnectionAsync(w)).Status);
        Assert.Empty(await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId).ToListAsync());
        Assert.Empty((await w.Admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections").EnumerateArray());
        Assert.Single(await JiraKit.IssuesAsync(w, project)); // the imported rows stay
        Assert.Equal(w.ConnectionId, (await db.Projects.AsNoTracking().SingleAsync(p => p.Id == project)).SourceConnectionId);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == "integration.disconnected" && a.TargetId == w.ConnectionId.ToString()));
        // Everything of a disconnected connection is closed to its people.
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.Http.GetAsync($"{w.Base}/spaces")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).StatusCode);
        // The site can be connected again: a new connection, and the old row is still there.
        var again = api.Jira.Account("Again", w.Admin.Email, w.Site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(w.Admin, api.Jira, again, await JiraKit.StartAsync(w.Admin, w.Site.Subdomain))));
        Assert.Equal(2, await CountConnectionsAsync(w.WorkspaceId));
    }

    [Fact]
    public async Task The_switch_for_automatic_invitations_is_changed_by_an_admin_only_and_is_audited()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ic-set-member");
        var other = await JiraKit.ConnectedAsync(api);

        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).StatusCode);
        Assert.False((await JiraKit.ConnectionAsync(w)).AutoInvite);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).StatusCode);

        Assert.True((await JiraKit.ConnectionAsync(w)).AutoInvite);
        Assert.True((await member.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/integrations")).GetProperty("connections")[0].GetProperty("autoInvite").GetBoolean());
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == "integration.changed");
        Assert.Equal(w.Admin.Id, audit.ActorUserId);
        Assert.True(audit.Detail!.RootElement.GetProperty("autoInvite").GetBoolean());
    }
}
