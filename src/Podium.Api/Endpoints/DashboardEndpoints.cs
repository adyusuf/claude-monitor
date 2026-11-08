using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>How many of something are in one state.</summary>
public sealed record StateCount(string State, int Count);

/// <summary>
/// The workspace dashboard's figures (08/11/2026): projects and sprints by status, remote runs by status and permission answers by outcome over
/// the last <see cref="DashboardView.Days"/> days, and open alerts. The machines themselves come from the machines list.
/// </summary>
public sealed record DashboardView(int Days, IReadOnlyList<StateCount> Projects, IReadOnlyList<StateCount> Sprints, IReadOnlyList<StateCount> Runs,
    IReadOnlyList<StateCount> Approvals, int OpenAlerts);

/// <summary>The dashboard: counts only, for any member of the workspace (viewer and up), who already reads every list they come from.</summary>
public static class DashboardEndpoints
{
    public const int DaysDefault = 30;
    public const int DaysMax = 90;

    /// <summary>The permission outcome of an answer a standing approval gave (ADR-0019), told apart from a person's allow.</summary>
    public const string AutoAllowed = "auto_allowed";

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{id:guid}/dashboard", Get);
    }

    private static async Task<IResult> Get(Guid id, int? days, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var ct = http.RequestAborted;
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, ct) is null) return Http.NotFound();
        var span = Math.Clamp(days ?? DaysDefault, 1, DaysMax);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-span);

        var projects = await db.Projects.AsNoTracking().Where(p => p.WorkspaceId == id)
            .GroupBy(p => p.Status).Select(g => new StateCount(g.Key, g.Count())).ToListAsync(ct);
        var sprints = await (from s in db.Sprints.AsNoTracking()
                             join p in db.Projects.AsNoTracking() on s.ProjectId equals p.Id
                             where s.WorkspaceId == id && p.Status == ManagedStatuses.Active
                             group s by s.Status into g
                             select new StateCount(g.Key, g.Count())).ToListAsync(ct);
        var runs = await db.RemoteRuns.AsNoTracking().Where(r => r.WorkspaceId == id && r.CreatedAt >= since)
            .GroupBy(r => r.Status).Select(g => new StateCount(g.Key, g.Count())).ToListAsync(ct);
        var answers = await db.PermissionRequests.AsNoTracking().Where(p => p.WorkspaceId == id && p.CreatedAt >= since)
            .GroupBy(p => new { p.Status, p.Decision, Rule = p.AnsweredByRule != null })
            .Select(g => new { g.Key.Status, g.Key.Decision, g.Key.Rule, N = g.Count() }).ToListAsync(ct);
        var approvals = answers
            .GroupBy(a => a.Rule ? AutoAllowed
                : a.Status == PermissionStatuses.Answered ? a.Decision ?? PermissionDecisions.Deny
                : a.Status == PermissionStatuses.Open ? PermissionStatuses.Open : PermissionStatuses.Expired)
            .Select(g => new StateCount(g.Key, g.Sum(x => x.N))).ToList();
        var alerts = await db.MachineAlerts.AsNoTracking().CountAsync(a => a.WorkspaceId == id && a.State == AlertStates.Open, ct);
        return Results.Ok(new DashboardView(span, projects, sprints, runs, approvals, alerts));
    }
}
