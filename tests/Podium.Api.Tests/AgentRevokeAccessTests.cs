using System.Net;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Who may disconnect an agent: its own user and the workspace's admins, nobody else (the revoke rule in MachineEndpoints).</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentRevokeAccessTests(ApiFactory api)
{
    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task A_member_or_viewer_cannot_revoke_another_users_agent(string role)
    {
        var owner = await api.NewClient().SignedUpAsync("rv-owner");
        var agent = await owner.ConnectAgentAsync();
        var peer = await RemoteKit.MemberAsync(api, owner, "rv-peer", role);

        var response = await peer.PostAsync($"/api/agents/{agent.Tokens.AgentId}/revoke");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var db = api.Db();
        Assert.Equal(AgentStatuses.Active, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task An_admin_revokes_a_members_agent_and_its_tokens_stop_working()
    {
        var owner = await api.NewClient().SignedUpAsync("rv-ws-owner");
        var admin = await RemoteKit.MemberAsync(api, owner, "rv-admin", "admin");
        var member = await RemoteKit.MemberAsync(api, owner, "rv-member");
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);

        var response = await admin.PostAsync($"/api/agents/{agent.Tokens.AgentId}/revoke");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId);
        Assert.Equal((AgentStatuses.Revoked, admin.Id), (row.Status, row.RevokedBy));
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task A_manual_revoke_writes_an_audit_row_naming_the_workspace_the_actor_and_the_agent()
    {
        var owner = await api.NewClient().SignedUpAsync("rv-audit-owner");
        var admin = await RemoteKit.MemberAsync(api, owner, "rv-audit-admin", "admin");
        var agent = await owner.ConnectAgentAsync();

        (await admin.PostAsync($"/api/agents/{agent.Tokens.AgentId}/revoke")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var audit = Assert.Single(db.AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.AgentRevoked && a.TargetId == agent.Tokens.AgentId.ToString()));
        Assert.Equal((owner.WorkspaceId, admin.Id, "agent"), (audit.WorkspaceId, audit.ActorUserId, audit.TargetType));
        Assert.Null(audit.Detail);
    }
}
