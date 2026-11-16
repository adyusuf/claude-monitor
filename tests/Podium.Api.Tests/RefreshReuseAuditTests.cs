using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>A copied refresh token is the signal an admin must see: its audit row lands in the agent's workspace, the agent's stream is told and
/// the agent is revoked.</summary>
[Collection(ApiGroup.Name)]
public sealed class RefreshReuseAuditTests(ApiFactory api)
{
    [Fact]
    public async Task Reuse_writes_an_audit_row_with_the_workspace_and_stops_the_stream()
    {
        var owner = await api.NewClient().SignedUpAsync("reuse-audit");
        var agent = await owner.ConnectAgentAsync();
        using var stream = await RemoteKit.OpenAsync(agent.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Ready);
        var http = api.CreateClient();
        Task<HttpResponseMessage> Refresh() => http.PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(agent.Tokens.RefreshToken), TestUser.Json);

        Assert.Equal(HttpStatusCode.OK, (await Refresh()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh()).StatusCode);

        await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Revoked);
        var audit = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/audit");
        var row = Assert.Single(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("action").GetString() == AuditActions.RefreshReuse);
        Assert.Equal(agent.Tokens.AgentId, row.GetProperty("actorAgentId").GetGuid());

        // F-207: the agent itself is revoked, so the machines list stops showing it as active with dead tokens.
        var listed = Assert.Single((await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/agents")).EnumerateArray());
        Assert.Equal(AgentStatuses.Revoked, listed.GetProperty("status").GetString());
        await using var db = api.Db();
        var stored = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == agent.Tokens.AgentId);
        Assert.Equal(AgentStatuses.Revoked, stored.Status);
        Assert.NotNull(stored.RevokedAt);
        Assert.Null(stored.RevokedBy); // no person revoked it: the reuse did
    }
}
