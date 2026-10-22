using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Projects;
using Podium.Api.Security;
using Podium.Api.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Endpoints;

/// <summary>Sprints of a managed project (ADR-0011 §5): planned, active, closed. Rights come from <see cref="ProjectAccess"/>.</summary>
public static class SprintEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/projects/{id:guid}/sprints", List);
        g.MapPost("/projects/{id:guid}/sprints", Create);
        g.MapGet("/sprints/{id:guid}", Get);
        g.MapPut("/sprints/{id:guid}", Update);
        g.MapDelete("/sprints/{id:guid}", Delete);
        g.MapPost("/sprints/{id:guid}/start", Start);
        g.MapPost("/sprints/{id:guid}/close", Close);
    }

    private static async Task<IResult> List(Guid id, string? status, Guid? application, string? cursor, int? limit, HttpContext http, PodiumDb db, ApiConfig config)
    {
        if (await ProjectAccess.OfAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.Sprints.AsNoTracking().Where(s => s.ProjectId == id && s.ArchivedAt == null);
        if (status is { Length: > 0 }) query = query.Where(s => s.Status == status);
        if (application is { } app) query = query.Where(s => s.ApplicationId == app);
        if (int.TryParse(cursor, out var before)) query = query.Where(s => s.Number < before);
        var page = await query.OrderByDescending(s => s.Number).Take(take + 1).ToListAsync(http.RequestAborted);
        var shown = page.Take(take).ToList();
        var rows = await SprintViews.SummariesAsync(db, shown, http.RequestAborted);
        return Results.Ok(new Page<SprintSummary>(rows, page.Count > take ? shown[^1].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }

    private static async Task<IResult> Create(Guid id, SprintInput req, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEditApplications) return Results.Forbid();
        var project = grant.Project;
        if (project.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (SprintRules.Check(req) is { } invalid) return invalid;
        if (req.ApplicationId is { } app
            && !await db.Applications.AnyAsync(a => a.Id == app && a.ProjectId == id && a.Status == ManagedStatuses.Active, http.RequestAborted))
        {
            return Http.Invalid("applicationId", "invalid_application");
        }

        if (await SprintRules.CheckReferencesAsync(db, project, req.ApplicationId, req, http.RequestAborted) is { } bad) return bad;

        var now = clock.GetUtcNow();
        var sprint = new Sprint { WorkspaceId = project.WorkspaceId, ProjectId = id, ApplicationId = req.ApplicationId, CreatedBy = userId, CreatedAt = now };
        Fill(sprint, req, userId, now);
        if (await writes.CreateAsync(userId, project, sprint, http.RequestAborted) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        await IssueEndpoints.LockProjectAsync(db, id, http.RequestAborted);
        sprint.Number = (await db.Sprints.Where(s => s.ProjectId == id).MaxAsync(s => (int?)s.Number, http.RequestAborted) ?? 0) + 1;
        db.Sprints.Add(sprint);
        SetTeam(db, sprint.Id, [], req.TeamUserIds, now);
        Audit.Add(db, http, clock, AuditActions.SprintCreated, project.WorkspaceId, userId, targetType: "sprint", targetId: sprint.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.Created($"/api/sprints/{sprint.Id}", await SprintViews.ViewAsync(db, sprint, true, http.RequestAborted));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, PodiumDb db)
    {
        if (await ProjectAccess.SprintAsync(db, http.User.UserId(), id, http.RequestAborted) is not { } f) return Http.NotFound();
        return Results.Ok(await SprintViews.ViewAsync(db, f.Sprint, f.Grant.CanEditApplications, http.RequestAborted));
    }

    /// <summary>Replaces the definition (see the note in SprintModels). A closed sprint is read-only; the scope never changes.</summary>
    private static async Task<IResult> Update(Guid id, SprintInput req, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.SprintAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var sprint = f.Sprint;
        if (sprint.Status == SprintStatuses.Closed) return Http.Invalid("status", "closed");
        if (SprintRules.Check(req) is { } invalid) return invalid;
        if (await SprintRules.CheckReferencesAsync(db, f.Grant.Project, sprint.ApplicationId, req, http.RequestAborted) is { } bad) return bad;
        var now = clock.GetUtcNow();
        if (await writes.UpdateAsync(userId, sprint, ProjectRules.Clean(req.Name), ProjectRules.Clean(req.Goal), req.StartDate, req.EndDate, http.RequestAborted) is { } refused) return refused;
        Fill(sprint, req, userId, now);
        var current = await db.SprintMembers.Where(m => m.SprintId == id).ToListAsync(http.RequestAborted);
        SetTeam(db, id, current, req.TeamUserIds, now);
        Audit.Add(db, http, clock, AuditActions.SprintUpdated, sprint.WorkspaceId, userId, targetType: "sprint", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>A planned sprint is archived and its issues go back to the backlog.</summary>
    private static async Task<IResult> Delete(Guid id, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.SprintAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var sprint = f.Sprint;
        if (sprint.Status != SprintStatuses.Planned) return Http.Invalid("status", "not_planned");
        if (await writes.DeleteAsync(userId, sprint, http.RequestAborted) is { } refused) return refused;
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var now = clock.GetUtcNow();
        await db.Issues.Where(i => i.SprintId == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.SprintId, (Guid?)null).SetProperty(i => i.UpdatedAt, now), http.RequestAborted);
        sprint.ArchivedAt = now;
        sprint.UpdatedAt = now;
        sprint.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.SprintArchived, sprint.WorkspaceId, userId, targetType: "sprint", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Start(Guid id, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.SprintAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var sprint = f.Sprint;
        if (sprint.Status != SprintStatuses.Planned) return Http.Invalid("status", "not_planned");
        if (sprint.StartDate is null || sprint.EndDate is null) return Http.Invalid("startDate", "dates_required");
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        await IssueEndpoints.LockProjectAsync(db, sprint.ProjectId, http.RequestAborted);
        // A sprint mirrored from a tool may run next to others, as the tool allows (ADR-0015 §4).
        var taken = sprint.SourceConnectionId is null && await db.Sprints.AnyAsync(s => s.ProjectId == sprint.ProjectId && s.Status == SprintStatuses.Active && s.ArchivedAt == null
            && s.ApplicationId == sprint.ApplicationId, http.RequestAborted);
        if (taken) return Http.Invalid("status", "sprint_active");
        if (await writes.StartAsync(userId, sprint, http.RequestAborted) is { } refused) return refused;
        var now = clock.GetUtcNow();
        sprint.Status = SprintStatuses.Active;
        sprint.StartedAt = now;
        sprint.UpdatedAt = now;
        sprint.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.SprintStarted, sprint.WorkspaceId, userId, targetType: "sprint", targetId: id);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Http.Invalid("status", "sprint_active");
        }

        await tx.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>Closes an active sprint. Its unfinished issues go to a planned sprint of the same scope, or back to the backlog.</summary>
    private static async Task<IResult> Close(Guid id, CloseSprintInput req, HttpContext http, PodiumDb db, TimeProvider clock, SprintWrites writes)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.SprintAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var sprint = f.Sprint;
        if (sprint.Status != SprintStatuses.Active) return Http.Invalid("status", "not_active");
        if (req.MoveUnfinishedTo is { } target
            && !await db.Sprints.AnyAsync(s => s.Id == target && s.ProjectId == sprint.ProjectId && s.Status == SprintStatuses.Planned && s.ArchivedAt == null
                && s.ApplicationId == sprint.ApplicationId, http.RequestAborted))
        {
            return Http.Invalid("moveUnfinishedTo", "invalid_sprint");
        }

        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var now = clock.GetUtcNow();
        var unfinished = await db.Issues.Where(i => i.SprintId == id && i.ArchivedAt == null && i.Status != IssueStatuses.Done && i.Status != IssueStatuses.Cancelled)
            .ToListAsync(http.RequestAborted);
        var moveTo = req.MoveUnfinishedTo is { } moveId ? await db.Sprints.AsNoTracking().FirstAsync(s => s.Id == moveId, http.RequestAborted) : null;
        if (await writes.CloseAsync(userId, sprint, unfinished, moveTo, http.RequestAborted) is { } refused) return refused;
        foreach (var issue in unfinished)
        {
            SprintIssueEndpoints.Release(issue, req.MoveUnfinishedTo, userId, now);
        }

        sprint.Status = SprintStatuses.Closed;
        sprint.ClosedAt = now;
        sprint.UpdatedAt = now;
        sprint.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.SprintClosed, sprint.WorkspaceId, userId, targetType: "sprint", targetId: id, detail: new { moved = unfinished.Count });
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static void Fill(Sprint s, SprintInput i, Guid userId, DateTimeOffset now)
    {
        s.Name = ProjectRules.Clean(i.Name);
        s.Goal = ProjectRules.Clean(i.Goal);
        s.StartDate = i.StartDate;
        s.EndDate = i.EndDate;
        s.Capacity = i.Capacity;
        s.ReleaseId = i.ReleaseId;
        s.UpdatedBy = userId;
        s.UpdatedAt = now;
    }

    /// <summary>Makes the team the given people: the ones no longer named are removed, the new ones added.</summary>
    private static void SetTeam(PodiumDb db, Guid sprintId, IReadOnlyCollection<SprintMember> current, List<Guid>? wanted, DateTimeOffset now)
    {
        var keep = (wanted ?? []).ToHashSet();
        db.SprintMembers.RemoveRange(current.Where(m => !keep.Contains(m.UserId)));
        foreach (var user in keep.Where(u => current.All(m => m.UserId != u)))
        {
            db.SprintMembers.Add(new SprintMember { SprintId = sprintId, UserId = user, AddedAt = now });
        }
    }
}
