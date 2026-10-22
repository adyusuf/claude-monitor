using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Projects;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>The issues of a sprint, and the quick status change used on the sprint page (ADR-0011 §5).</summary>
public static class SprintIssueEndpoints
{
    public const int AddMax = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapPost("/sprints/{id:guid}/issues", Add);
        g.MapDelete("/sprints/{id:guid}/issues/{issueId:guid}", Remove);
        g.MapPut("/issues/{id:guid}/status", SetStatus);
    }

    /// <summary>Puts issues of the backlog into a planned or active sprint. An issue already in a sprint stays out of reach: take it out first.</summary>
    private static async Task<IResult> Add(Guid id, SprintIssuesInput req, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.SprintAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var sprint = f.Sprint;
        if (sprint.Status == SprintStatuses.Closed) return Http.Invalid("status", "closed");
        var ids = req.IssueIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return Http.Invalid("issueIds", "invalid_list");
        if (ids.Count > AddMax) return Http.Invalid("issueIds", "limit_reached");

        var issues = await db.Issues.Where(i => ids.Contains(i.Id) && i.ProjectId == sprint.ProjectId && i.ArchivedAt == null).ToListAsync(http.RequestAborted);
        if (issues.Count != ids.Count || issues.Any(i => i.SprintId is not null && i.SprintId != id)) return Http.Invalid("issueIds", "invalid_issue");
        if (sprint.ApplicationId is { } app && issues.Any(i => i.ApplicationId != app)) return Http.Invalid("issueIds", "wrong_scope");

        if (await writes.AddIssuesAsync(userId, sprint, issues, http.RequestAborted) is { } refused) return refused;
        var now = clock.GetUtcNow();
        foreach (var issue in issues)
        {
            issue.SprintId = id;
            issue.UpdatedAt = now;
            issue.UpdatedBy = userId;
        }

        Audit.Add(db, http, clock, AuditActions.SprintIssuesChanged, sprint.WorkspaceId, userId, targetType: "sprint", targetId: id, detail: new { added = issues.Count });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Remove(Guid id, Guid issueId, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.SprintAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var sprint = f.Sprint;
        if (sprint.Status == SprintStatuses.Closed) return Http.Invalid("status", "closed");
        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == issueId && i.SprintId == id, http.RequestAborted);
        if (issue is null) return Http.NotFound();
        if (await writes.RemoveIssueAsync(userId, sprint, issue, http.RequestAborted) is { } refused) return refused;
        Release(issue, null, userId, clock.GetUtcNow());
        Audit.Add(db, http, clock, AuditActions.SprintIssuesChanged, sprint.WorkspaceId, userId, targetType: "sprint", targetId: id, detail: new { removed = 1 });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> SetStatus(Guid id, StatusInput req, HttpContext http, PodiumDb db, TimeProvider clock, IssueWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (f.Issue.ArchivedAt is not null) return Http.Invalid("status", "archived");
        if (req.Status is null || !IssueStatuses.All.Contains(req.Status)) return Http.Invalid("status", "invalid_value");
        if (await writes.StatusAsync(userId, f.Grant.Project, f.Issue, req.Status, http.RequestAborted) is { } refused) return refused;
        f.Issue.Status = req.Status;
        f.Issue.UpdatedAt = clock.GetUtcNow();
        f.Issue.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.IssueUpdated, f.Issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>Takes an issue out of its sprint: into another sprint, or back to the backlog where work in flight becomes "ready".</summary>
    internal static void Release(Issue issue, Guid? toSprint, Guid userId, DateTimeOffset now)
    {
        issue.SprintId = toSprint;
        // An issue that mirrors the tool's keeps the tool's status: leaving a sprint does not change it there.
        if (toSprint is null && issue.ExternalKey is null && issue.Status is IssueStatuses.InProgress or IssueStatuses.InReview)
        {
            issue.Status = IssueStatuses.Ready;
        }

        issue.UpdatedAt = now;
        issue.UpdatedBy = userId;
    }
}
