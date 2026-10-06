using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>Machines' metrics and alerts on the web; any member of the workspace (viewer and up) reads them.</summary>
public static class MachineMetricsEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{id:guid}/machines", Machines);
        g.MapGet("/workspaces/{id:guid}/alerts", Alerts);
        g.MapGet("/agents/{id:guid}/metrics", Metrics);
    }

    private static async Task<IResult> Machines(Guid id, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await MachineQueries.MachinesAsync(db, config, id, clock.GetUtcNow(), http.RequestAborted));
    }

    private static async Task<IResult> Alerts(Guid id, Guid? agent, bool? resolved, HttpContext http, MonitorDb db)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await MachineQueries.AlertsAsync(db, id, agent, resolved ?? false, AgentMachineEndpoints.AlertsMax,
            http.RequestAborted));
    }

    private static async Task<IResult> Metrics(Guid id, int? minutes, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock)
    {
        var ws = await db.Agents.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.WorkspaceId).FirstOrDefaultAsync(http.RequestAborted);
        if (ws is not { } workspaceId || await Access.MemberAsync(db, http.User.UserId(), workspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        return Results.Ok(await MachineQueries.MetricsAsync(db, config, workspaceId, id, minutes ?? 60, clock.GetUtcNow(), http.RequestAborted));
    }
}
