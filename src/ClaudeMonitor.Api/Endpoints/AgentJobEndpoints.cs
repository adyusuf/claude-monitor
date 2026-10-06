using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Jobs as a requester's agent calls them (ADR-0004): it proposes one (only a proposal until the target's owner
/// approves it) and lists the workspace's active and proposed ones. Running one is POST /agent/runs with its JobId.
/// </summary>
public static class AgentJobEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate);
        g.MapPost("/jobs", Propose);
        g.MapGet("/jobs", List);
    }

    private static Task<IResult> Propose(JobProposal req, HttpContext http, JobService jobs) =>
        jobs.ProposeAsync(req, http.User.AgentId(), http.User.UserId(), http.User.AgentWorkspaceId(), http);

    private static async Task<IResult> List(Guid? target, HttpContext http, MonitorDb db)
    {
        var ws = http.User.AgentWorkspaceId();
        if (await Access.MemberAsync(db, http.User.UserId(), ws, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await JobQueries.ForAgentAsync(db, ws, target, http.RequestAborted));
    }
}
