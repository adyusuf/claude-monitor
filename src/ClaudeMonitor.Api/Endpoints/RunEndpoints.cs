using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Remote runs on the web (ADR-0005). Any member reads who ran what where and when; the command and its output only
/// the requester and the target's owner; only the owner approves or denies; requester or owner cancels.
/// </summary>
public static class RunEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{id:guid}/runs", List);
        g.MapGet("/runs/{id:guid}", Get);
        g.MapGet("/runs/{id:guid}/output", Output);
        g.MapPost("/runs/{id:guid}/approve", Approve);
        g.MapPost("/runs/{id:guid}/deny", Deny);
        g.MapPost("/runs/{id:guid}/cancel", Cancel);
    }

    private static async Task<IResult> List(Guid id, Guid? agent, DateTimeOffset? before, int? limit, HttpContext http, MonitorDb db,
        ApiConfig config, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await Access.MemberAsync(db, userId, id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await RunQueries.ForWebAsync(db, config, id, userId, agent, null, before, Http.Limit(limit, config),
            clock.GetUtcNow(), http.RequestAborted));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var workspaceId = await db.RemoteRuns.AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.WorkspaceId)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (workspaceId is not { } ws || await Access.MemberAsync(db, userId, ws, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        var views = await RunQueries.ForWebAsync(db, config, ws, userId, null, id, null, 1, clock.GetUtcNow(), http.RequestAborted);
        return views.Count == 1 ? Results.Ok(views[0]) : Http.NotFound();
    }

    private static async Task<IResult> Output(Guid id, int? after, int? limit, HttpContext http, MonitorDb db)
    {
        var userId = http.User.UserId();
        var run = await db.RemoteRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && (r.RequesterUserId == userId || r.TargetUserId == userId), http.RequestAborted);
        if (run is null || await Access.MemberAsync(db, userId, run.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        return Results.Ok(await RunQueries.OutputAsync(db, id, after ?? -1,
            Math.Clamp(limit ?? AgentRunEndpoints.OutputPageMax, 1, AgentRunEndpoints.OutputPageMax),
            RunStatuses.Final.Contains(run.Status), http.RequestAborted));
    }

    private static Task<IResult> Approve(Guid id, RunApproval req, HttpContext http, RunDecisions decisions) =>
        decisions.ApproveAsync(id, req, http);

    private static Task<IResult> Deny(Guid id, RunDenial req, HttpContext http, RunDecisions decisions) =>
        decisions.DenyAsync(id, req, http);

    private static Task<IResult> Cancel(Guid id, HttpContext http, RunDecisions decisions) =>
        decisions.CancelAsync(id, http.User.UserId(), null, http);
}
