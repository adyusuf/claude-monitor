using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Grants on the web (ADR-0004). A member reads the grants of an agent: its owner all of them, anyone else only their
/// own as grantee. Only the target's owner creates, approves or denies (approving and creating need a recent sign-in
/// or a code); the owner and the grantee revoke.
/// </summary>
public static class GrantEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/agents/{id:guid}/grants", List);
        g.MapPost("/agents/{id:guid}/grants", Create);
        g.MapPost("/grants/{id:guid}/approve", Approve);
        g.MapPost("/grants/{id:guid}/deny", Deny);
        g.MapPost("/grants/{id:guid}/revoke", Revoke);
    }

    private static async Task<IResult> List(Guid id, DateTimeOffset? before, int? limit, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        return Results.Ok(await GrantQueries.ForWebAsync(db, id, userId, agent.UserId == userId, null, before, Http.Limit(limit, config),
            clock.GetUtcNow(), http.RequestAborted));
    }

    private static Task<IResult> Create(Guid id, WebGrantRequest req, HttpContext http, GrantService grants) =>
        grants.CreateAsync(id, req, http);

    private static Task<IResult> Approve(Guid id, GrantApproval? req, HttpContext http, GrantService grants) =>
        grants.ApproveAsync(id, req, http);

    private static Task<IResult> Deny(Guid id, HttpContext http, GrantService grants) => grants.DenyAsync(id, http);

    private static Task<IResult> Revoke(Guid id, HttpContext http, GrantService grants) => grants.RevokeAsync(id, http);
}
