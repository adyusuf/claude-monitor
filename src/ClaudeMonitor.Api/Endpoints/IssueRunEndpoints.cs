using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Projects;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Remote runs attached to issues (ADR-0011 §6): a failed test run becomes a bug, a green one closes the loop. The link shows what ran where and
/// how it ended; the command and the output stay behind the rules of the run itself (ADR-0005).
/// </summary>
public static class IssueRunEndpoints
{
    public const int PerIssueMax = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/issues/{id:guid}/runs", Runs);
        g.MapPost("/issues/{id:guid}/runs", Link);
        g.MapDelete("/issues/{id:guid}/runs/{runId:guid}", Unlink);
        g.MapGet("/runs/{id:guid}/issues", IssuesOfRun);
    }

    private static async Task<IResult> Runs(Guid id, HttpContext http, MonitorDb db)
    {
        if (await ProjectAccess.IssueAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await (from l in db.IssueRuns.AsNoTracking()
                          join r in db.RemoteRuns.AsNoTracking() on l.RunId equals r.Id
                          join a in db.Agents.AsNoTracking() on r.TargetAgentId equals a.Id
                          join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                          where l.IssueId == id
                          orderby r.CreatedAt descending, r.Id descending
                          select new IssueRunView(r.Id, r.Kind, r.Status, r.ExitCode, r.TestsPassed, r.TestsFailed, r.TestsSkipped, m.Hostname, r.ProjectKey,
                              r.CreatedAt, r.EndedAt, l.LinkedAt)).Take(PerIssueMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Link(Guid id, IssueRunInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (f.Issue.ArchivedAt is not null) return Http.Invalid("status", "archived");
        if (req.RunId is not { } runId || !await db.RemoteRuns.AnyAsync(r => r.Id == runId && r.WorkspaceId == f.Issue.WorkspaceId, http.RequestAborted))
        {
            return Http.Invalid("runId", "invalid_run");
        }

        if (await db.IssueRuns.AnyAsync(l => l.IssueId == id && l.RunId == runId, http.RequestAborted)) return Http.Invalid("runId", "duplicate_link");
        if (await db.IssueRuns.CountAsync(l => l.IssueId == id, http.RequestAborted) >= PerIssueMax) return Http.Invalid("runId", "limit_reached");
        db.IssueRuns.Add(new IssueRun { IssueId = id, RunId = runId, LinkedBy = userId, LinkedAt = clock.GetUtcNow() });
        Audit.Add(db, http, clock, AuditActions.IssueRunLinkChanged, f.Issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Unlink(Guid id, Guid runId, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var link = await db.IssueRuns.FirstOrDefaultAsync(l => l.IssueId == id && l.RunId == runId, http.RequestAborted);
        if (link is null) return Http.NotFound();
        db.IssueRuns.Remove(link);
        Audit.Add(db, http, clock, AuditActions.IssueRunLinkChanged, f.Issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>The issues a run is linked to, for the run list; only issues of projects the caller can see in the run's workspace.</summary>
    private static async Task<IResult> IssuesOfRun(Guid id, HttpContext http, MonitorDb db)
    {
        var workspaceId = await db.RemoteRuns.AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.WorkspaceId).FirstOrDefaultAsync(http.RequestAborted);
        if (workspaceId is not { } ws || await Access.MemberAsync(db, http.User.UserId(), ws, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var rows = await (from l in db.IssueRuns.AsNoTracking()
                          join i in db.Issues.AsNoTracking() on l.IssueId equals i.Id
                          where l.RunId == id && i.WorkspaceId == ws && i.ArchivedAt == null
                          orderby i.ProjectId, i.Number
                          select new RunIssueView(i.Id, i.ProjectId, i.Number, i.Title, i.Status)).Take(PerIssueMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }
}
