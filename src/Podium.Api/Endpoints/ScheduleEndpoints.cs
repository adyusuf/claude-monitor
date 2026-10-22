using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>Job schedules on the web (ADR-0010 §3). Any member reads a target's schedules; the owner creates and resumes; the owner or an admin pauses and ends.</summary>
public static class ScheduleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/agents/{id:guid}/schedules", List);
        g.MapPost("/schedules", (ScheduleCreate req, HttpContext http, ScheduleService schedules) => schedules.CreateAsync(req, http));
        g.MapPost("/schedules/{id:guid}/pause", (Guid id, HttpContext http, ScheduleService schedules) => schedules.PauseAsync(id, http));
        g.MapPost("/schedules/{id:guid}/resume", (Guid id, ScheduleResume? req, HttpContext http, ScheduleService schedules) => schedules.ResumeAsync(id, req, http));
        g.MapPost("/schedules/{id:guid}/end", (Guid id, HttpContext http, ScheduleService schedules) => schedules.EndAsync(id, http));
    }

    private static async Task<IResult> List(Guid id, HttpContext http, PodiumDb db)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await ScheduleQueries.ForTargetAsync(db, id, userId, null, http.RequestAborted));
    }
}
