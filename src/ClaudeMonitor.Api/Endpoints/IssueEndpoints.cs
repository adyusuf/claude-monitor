using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Projects;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>Issues of a managed project and its backlog (ADR-0011 §4). Rights come from <see cref="ProjectAccess"/>; an issue is archived, never deleted.</summary>
public static class IssueEndpoints
{
    public const double Step = 1024;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/projects/{id:guid}/issues", List);
        g.MapPost("/projects/{id:guid}/issues", Create);
        g.MapGet("/issues/{id:guid}", Get);
        g.MapPut("/issues/{id:guid}", Update);
        g.MapDelete("/issues/{id:guid}", Archive);
        g.MapPost("/issues/{id:guid}/restore", Restore);
        g.MapPut("/issues/{id:guid}/position", Move);
    }

    private static async Task<IResult> List(Guid id, string? q, string? type, string? status, Guid? assignee, Guid? application, Guid? component,
        Guid? parent, Guid? release, Guid? sprint, string? scope, bool? archived, string? cursor, int? limit, HttpContext http, MonitorDb db, ApiConfig config)
    {
        if (await ProjectAccess.OfAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.Issues.AsNoTracking().Where(i => i.ProjectId == id);
        query = archived == true ? query.Where(i => i.ArchivedAt != null) : query.Where(i => i.ArchivedAt == null);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(i => EF.Functions.Like(i.TitleSearch, SearchText.ContainsPattern(q), "\\"));
        if (type is { Length: > 0 }) query = query.Where(i => i.Type == type);
        if (status is { Length: > 0 }) query = query.Where(i => i.Status == status);
        if (assignee is { } a) query = query.Where(i => i.AssigneeUserId == a);
        if (application is { } app) query = query.Where(i => i.ApplicationId == app);
        if (component is { } c) query = query.Where(i => i.ComponentId == c);
        if (parent is { } p) query = query.Where(i => i.ParentId == p);
        if (release is { } r) query = query.Where(i => i.ReleaseId == r);
        if (sprint is { } sp) query = query.Where(i => i.SprintId == sp);
        if (scope == "backlog") query = query.Where(i => i.SprintId == null && (i.Status == IssueStatuses.Backlog || i.Status == IssueStatuses.Ready));
        if (PositionCursor.Parse(cursor) is { } after)
        {
            query = query.Where(i => i.Position > after.Position || (i.Position == after.Position && i.Id.CompareTo(after.Id) > 0));
        }

        var page = await query.OrderBy(i => i.Position).ThenBy(i => i.Id).Take(take + 1).ToListAsync(http.RequestAborted);
        var shown = page.Take(take).ToList();
        var assigneeName = await IssueViews.AssigneeNamesAsync(db, shown, http.RequestAborted);
        var rows = shown.Select(i => IssueViews.Summary(i, assigneeName(i))).ToList();
        var next = page.Count > take ? PositionCursor.Of(shown[^1].Position, shown[^1].Id) : null;
        return Results.Ok(new Page<IssueSummary>(rows, next));
    }

    private static async Task<IResult> Create(Guid id, IssueInput req, HttpContext http, MonitorDb db, TimeProvider clock, IssueWrites writes)
    {
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEditApplications) return Results.Forbid();
        var project = grant.Project;
        if (project.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (IssueRules.Check(req, project) is { } invalid) return invalid;
        var type = req.Type ?? project.ItemTypes[0];
        if (IssueRules.CheckBugFields(req, type) is { } bug) return bug;

        var now = clock.GetUtcNow();
        var issue = new Issue { WorkspaceId = project.WorkspaceId, ProjectId = id, CreatedBy = userId, CreatedAt = now };
        Fill(issue, req, project, type, userId, now);
        if (await IssueRules.CheckReferencesAsync(db, project, issue, type, http.RequestAborted) is { } bad) return bad;
        if (await writes.CreateAsync(userId, project, issue, http.RequestAborted) is { } refused) return refused;

        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        await LockProjectAsync(db, id, http.RequestAborted);
        issue.Number = (await db.Issues.Where(i => i.ProjectId == id).MaxAsync(i => (int?)i.Number, http.RequestAborted) ?? 0) + 1;
        issue.Position = (await db.Issues.Where(i => i.ProjectId == id).MaxAsync(i => (double?)i.Position, http.RequestAborted) ?? 0) + Step;
        db.Issues.Add(issue);
        Audit.Add(db, http, clock, AuditActions.IssueCreated, project.WorkspaceId, userId, targetType: "issue", targetId: issue.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.Created($"/api/issues/{issue.Id}", await IssueViews.ViewAsync(db, issue, true, http.RequestAborted));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        if (await ProjectAccess.IssueAsync(db, http.User.UserId(), id, http.RequestAborted) is not { } f) return Http.NotFound();
        return Results.Ok(await IssueViews.ViewAsync(db, f.Issue, f.Grant.CanEditApplications, http.RequestAborted));
    }

    /// <summary>Replaces the issue (see the note in IssueModels; an empty priority or estimate clears it).</summary>
    private static async Task<IResult> Update(Guid id, IssueInput req, HttpContext http, MonitorDb db, TimeProvider clock, IssueWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var issue = f.Issue;
        var project = f.Grant.Project;
        if (issue.ArchivedAt is not null) return Http.Invalid("status", "archived");
        if (IssueRules.Check(req, project) is { } invalid) return invalid;
        var type = req.Type ?? issue.Type;
        if (IssueRules.CheckBugFields(req, type) is { } bug) return bug;
        if (type != WorkItemTypes.Epic && issue.Type == WorkItemTypes.Epic && await db.Issues.AnyAsync(i => i.ParentId == id, http.RequestAborted))
        {
            return Http.Invalid("type", "invalid_value");
        }

        var before = IssueFacts.Of(issue);
        Fill(issue, req, project, type, userId, clock.GetUtcNow());
        if (await IssueRules.CheckReferencesAsync(db, project, issue, type, http.RequestAborted) is { } bad) return bad;
        if (await writes.UpdateAsync(userId, project, before, issue, http.RequestAborted) is { } refused) return refused;

        Audit.Add(db, http, clock, AuditActions.IssueUpdated, issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static Task<IResult> Archive(Guid id, HttpContext http, MonitorDb db, TimeProvider clock) => SetArchived(id, true, AuditActions.IssueArchived, http, db, clock);

    private static Task<IResult> Restore(Guid id, HttpContext http, MonitorDb db, TimeProvider clock) => SetArchived(id, false, AuditActions.IssueRestored, http, db, clock);

    private static async Task<IResult> SetArchived(Guid id, bool archive, string action, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (f.Issue.ExternalKey is not null) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.NoArchive);
        var now = clock.GetUtcNow();
        f.Issue.ArchivedAt = archive ? now : null;
        f.Issue.UpdatedAt = now;
        f.Issue.UpdatedBy = userId;
        Audit.Add(db, http, clock, action, f.Issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>Puts the issue after another one of its project, or at the top. The position is the middle of the two neighbours.</summary>
    private static async Task<IResult> Move(Guid id, PositionInput req, HttpContext http, MonitorDb db, TimeProvider clock, IssueWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var issue = f.Issue;
        var anchor = req.AfterIssueId is { } anchorId ? await db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == anchorId && i.ProjectId == issue.ProjectId, http.RequestAborted) : null;
        if (req.AfterIssueId is not null && anchor is null) return Http.Invalid("afterIssueId", "invalid_issue");
        if (await writes.RankAsync(userId, f.Grant.Project, issue, anchor, http.RequestAborted) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        await LockProjectAsync(db, issue.ProjectId, http.RequestAborted);
        await db.Entry(issue).ReloadAsync(http.RequestAborted);
        var others = db.Issues.Where(i => i.ProjectId == issue.ProjectId && i.Id != id);
        double position;
        if (req.AfterIssueId is { } afterId)
        {
            var after = await others.Where(i => i.Id == afterId).Select(i => (double?)i.Position).FirstOrDefaultAsync(http.RequestAborted);
            if (after is null) return Http.Invalid("afterIssueId", "invalid_issue");
            var next = await others.Where(i => i.Position > after).MinAsync(i => (double?)i.Position, http.RequestAborted);
            position = next is null ? after.Value + Step : (after.Value + next.Value) / 2;
            if (next is not null && (position <= after.Value || position >= next.Value)) position = await RenumberAsync(db, issue, afterId, http.RequestAborted);
        }
        else
        {
            position = (await others.MinAsync(i => (double?)i.Position, http.RequestAborted) ?? Step) - Step;
        }

        issue.Position = position;
        issue.UpdatedAt = clock.GetUtcNow();
        issue.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.IssueMoved, issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>The gap between two neighbours ran out: the project's issues get evenly spaced positions again, then the moved one goes after its anchor.</summary>
    private static async Task<double> RenumberAsync(MonitorDb db, Issue moved, Guid afterId, CancellationToken ct)
    {
        var rows = await db.Issues.Where(i => i.ProjectId == moved.ProjectId && i.Id != moved.Id).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync(ct);
        var at = 0;
        var result = Step;
        foreach (var row in rows)
        {
            row.Position = ++at * Step * 2;
            if (row.Id == afterId) result = row.Position + Step;
        }

        return result;
    }

    internal static Task<int> LockProjectAsync(MonitorDb db, Guid projectId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM projects WHERE id = {projectId} FOR UPDATE", ct);

    private static void Fill(Issue i, IssueInput r, ManagedProject project, string type, Guid userId, DateTimeOffset now)
    {
        i.Title = r.Title!.Trim();
        i.TitleSearch = SearchText.Normalize(i.Title);
        i.Type = type;
        if (r.Priority is not null) i.Priority = IssueRules.Clean(r.Priority);
        if (r.Estimate is not null) i.Estimate = string.IsNullOrWhiteSpace(r.Estimate) ? null : IssueRules.Estimate(r.Estimate, project.EstimationUnit);
        i.Status = r.Status ?? i.Status;
        i.AssigneeUserId = r.AssigneeUserId;
        i.ParentId = r.ParentId;
        i.ApplicationId = r.ApplicationId;
        i.ComponentId = r.ComponentId;
        i.ReleaseId = r.ReleaseId;
        i.Description = IssueRules.Clean(r.Description);
        i.AcceptanceCriteria = ProjectJson.Pack((r.AcceptanceCriteria ?? []).Select(c => new CriterionEntry(c.Given!.Trim(), c.When!.Trim(), c.Then!.Trim())).ToList());
        i.TechnicalDesign = IssueRules.Clean(r.TechnicalDesign);
        i.BugEnvironment = IssueRules.Clean(r.BugEnvironment);
        i.ReproSteps = IssueRules.Clean(r.ReproSteps);
        i.ExpectedResult = IssueRules.Clean(r.ExpectedResult);
        i.ActualResult = IssueRules.Clean(r.ActualResult);
        i.Severity = IssueRules.Clean(r.Severity);
        i.UpdatedBy = userId;
        i.UpdatedAt = now;
    }
}

/// <summary>A keyset cursor "position|guid" for lists ordered by backlog position.</summary>
public static class PositionCursor
{
    public static string Of(double position, Guid id) => $"{BitConverter.DoubleToInt64Bits(position)}.{id:N}";

    public static (double Position, Guid Id)? Parse(string? cursor)
    {
        var parts = cursor?.Split('.');
        return parts is [var bits, var id] && long.TryParse(bits, out var b) && Guid.TryParseExact(id, "N", out var g)
            && BitConverter.Int64BitsToDouble(b) is var p && double.IsFinite(p)
            ? (p, g)
            : null;
    }
}
