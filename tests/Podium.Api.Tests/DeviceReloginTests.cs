using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// F-211: approving a machine again for another workspace revokes the same user's active agents of the same machine key in the
/// other workspaces (agent row and tokens), with an audit row in the old workspace naming both agents and both workspaces.
/// Another user's agent and another machine key are never touched.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class DeviceReloginTests(ApiFactory api)
{
    private static async Task<Guid> SecondWorkspaceAsync(TestUser user)
    {
        var created = await user.PostAsync("/api/workspaces", new { name = "Second" });
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<string?> StatusAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.Status).FirstOrDefaultAsync();
    }

    private async Task<bool> HasLiveTokenAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.AgentTokens.AsNoTracking().AnyAsync(t => t.AgentId == agentId && t.RevokedAt == null);
    }

    [Fact]
    public async Task A_relogin_into_another_workspace_revokes_the_users_old_agent_of_that_machine_and_audits_it()
    {
        var user = await api.NewClient().SignedUpAsync("relogin");
        var second = await SecondWorkspaceAsync(user);
        var old = await user.ConnectAgentAsync();

        var fresh = await user.ConnectAgentAsync(second, old.MachineKey);

        Assert.Equal(AgentStatuses.Revoked, await StatusAsync(old.Tokens.AgentId));
        Assert.False(await HasLiveTokenAsync(old.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        var refused = await api.CreateClient().PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(old.Tokens.RefreshToken), TestUser.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(AgentStatuses.Active, await StatusAsync(fresh.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.NoContent, (await fresh.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);

        var audit = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/audit");
        var row = Assert.Single(audit.GetProperty("items").EnumerateArray(), a =>
            a.GetProperty("action").GetString() == AuditActions.AgentRevoked && a.GetProperty("targetId").GetString() == old.Tokens.AgentId.ToString());
        Assert.Equal(user.Id, row.GetProperty("actorUserId").GetGuid());
        var detail = row.GetProperty("detail");
        Assert.Equal(DeviceEndpoints.ReplacedByReloginReason, detail.GetProperty("reason").GetString());
        Assert.Equal(old.Tokens.AgentId, detail.GetProperty("oldAgentId").GetGuid());
        Assert.Equal(user.WorkspaceId, detail.GetProperty("oldWorkspaceId").GetGuid());
        Assert.Equal(fresh.Tokens.AgentId, detail.GetProperty("newAgentId").GetGuid());
        Assert.Equal(second, detail.GetProperty("newWorkspaceId").GetGuid());
    }

    [Fact]
    public async Task Another_users_agent_of_the_same_machine_key_stays_active()
    {
        var owner = await api.NewClient().SignedUpAsync("relogin-owner");
        var other = await api.NewClient().SignedUpAsync("relogin-other");
        var theirs = await other.ConnectAgentAsync();

        var mine = await owner.ConnectAgentAsync(machineKey: theirs.MachineKey);

        Assert.Equal(AgentStatuses.Active, await StatusAsync(theirs.Tokens.AgentId));
        Assert.True(await HasLiveTokenAsync(theirs.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.NoContent, (await theirs.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.Equal(AgentStatuses.Active, await StatusAsync(mine.Tokens.AgentId));
        var audit = await other.GetJsonAsync($"/api/workspaces/{other.WorkspaceId}/audit");
        Assert.DoesNotContain(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("action").GetString() == AuditActions.AgentRevoked);
    }

    [Fact]
    public async Task The_users_agent_of_another_machine_key_in_the_other_workspace_stays_active()
    {
        var user = await api.NewClient().SignedUpAsync("relogin-key");
        var second = await SecondWorkspaceAsync(user);
        var otherMachine = await user.ConnectAgentAsync();

        var fresh = await user.ConnectAgentAsync(second);

        Assert.NotEqual(otherMachine.MachineKey, fresh.MachineKey);
        Assert.Equal(AgentStatuses.Active, await StatusAsync(otherMachine.Tokens.AgentId));
        Assert.True(await HasLiveTokenAsync(otherMachine.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.NoContent, (await otherMachine.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        var audit = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/audit");
        Assert.DoesNotContain(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("action").GetString() == AuditActions.AgentRevoked);
    }
}
