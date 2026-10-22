using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>Setup and reading helpers of the agent action tests (ADR-0005, "Agent actions from the web"); every call goes through the public API.</summary>
public static class ActionKit
{
    public static Task<HttpResponseMessage> PostAsync(TestUser user, Guid agentId, string kind, string? code = null) =>
        user.PostAsync($"/api/agents/{agentId}/actions", new AgentActionRequest(kind, code));

    public static async Task<AgentActionView> CreateAsync(TestUser owner, Guid agentId, string kind, string? code = null)
    {
        var response = await PostAsync(owner, agentId, kind, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
    }

    public static Task<HttpResponseMessage> ReportAsync(TestAgent agent, Guid id, string status, string? result = null) =>
        agent.Http.PostAsJsonAsync($"/api/agent/actions/{id}/status", new AgentActionReport(status, result), TestUser.Json);

    /// <summary>The agent takes the action and finishes it: the two reports a daemon sends.</summary>
    public static async Task FinishAsync(TestAgent agent, Guid id, string status = AgentActionStatuses.Succeeded, string? result = "done")
    {
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, AgentActionStatuses.Delivered)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, status, result)).StatusCode);
    }

    public static async Task<AgentAction> RowAsync(ApiFactory api, Guid id)
    {
        await using var db = api.Db();
        return await db.AgentActions.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    public static async Task<List<AgentAction>> RowsAsync(ApiFactory api, Guid agentId)
    {
        await using var db = api.Db();
        return await db.AgentActions.AsNoTracking().Where(a => a.AgentId == agentId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
    }

    public static async Task<List<AgentActionMessage>> WaitingAsync(ApiFactory api, Guid agentId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await AgentActions.WaitingForAsync(scope.ServiceProvider.GetRequiredService<PodiumDb>(), agentId, api.Clock.GetUtcNow(), CancellationToken.None);
    }

    public static async Task<List<JsonElement>> ListAsync(TestUser owner, Guid agentId) =>
        (await owner.GetJsonAsync($"/api/agents/{agentId}/actions")).EnumerateArray().ToList();

    /// <summary>A row written straight to the database, for the cases that need a time the API would not produce.</summary>
    public static async Task<Guid> InsertAsync(ApiFactory api, RemoteTeam team, string kind, string status, DateTimeOffset createdAt, DateTimeOffset? finishedAt,
        string? result = null)
    {
        await using var db = api.Db();
        var row = new AgentAction
        {
            WorkspaceId = team.WorkspaceId,
            AgentId = team.TargetId,
            RequestedBy = team.Owner.Id,
            Kind = kind,
            Status = status,
            Result = result,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + AgentActionKinds.QueueTtl,
            FinishedAt = finishedAt,
        };
        db.AgentActions.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>A target the admin classified, so it is (or is not) a strict one: production or the database function.</summary>
    public static Task<RemoteTeam> StrictTeamAsync(ApiFactory api, string environment = "production", string function = "application") =>
        ScriptKit.TeamAsync(api, environment, function, ExecLevels.Argv);
}
