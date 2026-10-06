using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Grants as a requester's agent calls them (ADR-0005): it asks for one (only a request until the target's owner
/// approves it) and reads the grants its user holds. The agent's user must be at least a member of its workspace.
/// </summary>
public static class AgentGrantEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate);
        g.MapPost("/grants", Request);
        g.MapGet("/grants", List);
    }

    private static Task<IResult> Request(GrantRequest req, HttpContext http, GrantService grants) =>
        grants.RequestAsync(req, http.User.AgentId(), http.User.UserId(), http.User.AgentWorkspaceId(), http);

    private static async Task<IResult> List(Guid? target, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var ws = http.User.AgentWorkspaceId();
        if (await Access.MemberAsync(db, userId, ws, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await GrantQueries.ForGranteeAsync(db, userId, ws, target, clock.GetUtcNow(), http.RequestAborted));
    }
}
