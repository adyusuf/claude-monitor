using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Jobs on the web (ADR-0004). Any member reads the jobs of an agent; only the target's owner approves (a recent
/// sign-in or a code), denies or retires.
/// </summary>
public static class JobEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/agents/{id:guid}/jobs", List);
        g.MapPost("/jobs/{id:guid}/approve", Approve);
        g.MapPost("/jobs/{id:guid}/deny", Deny);
        g.MapPost("/jobs/{id:guid}/retire", Retire);
    }

    private static async Task<IResult> List(Guid id, DateTimeOffset? before, int? limit, HttpContext http, MonitorDb db, ApiConfig config)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        return Results.Ok(await JobQueries.ForWebAsync(db, id, userId, null, before, Http.Limit(limit, config), http.RequestAborted));
    }

    private static Task<IResult> Approve(Guid id, JobApproval? req, HttpContext http, JobService jobs) => jobs.ApproveAsync(id, req, http);

    private static Task<IResult> Deny(Guid id, HttpContext http, JobService jobs) => jobs.DenyAsync(id, http);

    private static Task<IResult> Retire(Guid id, HttpContext http, JobService jobs) => jobs.RetireAsync(id, http);
}
