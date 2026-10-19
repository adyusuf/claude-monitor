using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Projects;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Managed projects and their members (ADR-0011). Reading needs any workspace role; creating needs "member"; the rest follows
/// <see cref="ProjectAccess"/>. A project is archived, never deleted.
/// </summary>
public static class ProjectEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{ws:guid}/projects", List);
        g.MapPost("/workspaces/{ws:guid}/projects", Create);
        g.MapGet("/projects/{id:guid}", Get);
        g.MapPut("/projects/{id:guid}", Update);
        g.MapDelete("/projects/{id:guid}", Archive);
        g.MapPost("/projects/{id:guid}/restore", Restore);
        g.MapGet("/projects/{id:guid}/members", Members);
        g.MapPut("/projects/{id:guid}/members/{userId:guid}", PutMember);
        g.MapDelete("/projects/{id:guid}/members/{userId:guid}", RemoveMember);
    }

    private static async Task<IResult> List(Guid ws, string? q, string? status, string? cursor, int? limit, HttpContext http, MonitorDb db,
        ApiConfig config)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), ws, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.Projects.AsNoTracking().Where(p => p.WorkspaceId == ws);
        query = status switch
        {
            "all" => query,
            ManagedStatuses.Archived => query.Where(p => p.Status == ManagedStatuses.Archived),
            _ => query.Where(p => p.Status == ManagedStatuses.Active),
        };
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => EF.Functions.Like(p.NameSearch, SearchText.ContainsPattern(q), "\\"));
        if (Cursor.Parse(cursor) is { } after)
        {
            query = query.Where(p => p.CreatedAt < after.At || (p.CreatedAt == after.At && p.Id.CompareTo(after.Id) < 0));
        }

        var page = await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(take + 1).ToListAsync(http.RequestAborted);
        var shown = page.Take(take).ToList();
        var owners = await Names(db, shown.Select(p => p.OwnerUserId), http.RequestAborted);
        var ids = shown.Select(p => p.Id).ToList();
        var counts = await db.Applications.AsNoTracking().Where(a => ids.Contains(a.ProjectId) && a.Status == ManagedStatuses.Active)
            .GroupBy(a => a.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, http.RequestAborted);
        var rows = shown.Select(p => ProjectViews.Summary(p, owners.GetValueOrDefault(p.OwnerUserId), counts.GetValueOrDefault(p.Id))).ToList();
        var next = page.Count > take ? Cursor.Of(shown[^1].CreatedAt, shown[^1].Id) : null;
        return Results.Ok(new Page<ProjectSummary>(rows, next));
    }

    private static async Task<IResult> Create(Guid ws, ProjectInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var member = await Access.MemberAsync(db, userId, ws, Roles.Viewer, http.RequestAborted);
        if (member is null) return Http.NotFound();
        if (Roles.Rank(member.Role) < Roles.Rank(Roles.Member)) return Results.Forbid();
        if (ProjectRules.Check(req) is { } invalid) return invalid;
        var owner = req.OwnerUserId ?? userId;
        if (!await ProjectRules.IsWorkerAsync(db, owner, ws, http.RequestAborted)) return Http.Invalid("ownerUserId", "not_a_member");

        var now = clock.GetUtcNow();
        var project = new ManagedProject { WorkspaceId = ws, OwnerUserId = owner, CreatedBy = userId, CreatedAt = now };
        Fill(project, req, userId, now);
        db.Projects.Add(project);
        Audit.Add(db, http, clock, AuditActions.ProjectCreated, ws, userId, targetType: "project", targetId: project.Id);
        if (await SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        var access = ProjectAccess.Decide(project, member, null);
        return Results.Created($"/api/projects/{project.Id}", ProjectViews.View(project, (await Names(db, [owner], http.RequestAborted)).GetValueOrDefault(owner), access.View));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        var grant = await ProjectAccess.OfAsync(db, http.User.UserId(), id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        var owner = grant.Project.OwnerUserId;
        return Results.Ok(ProjectViews.View(grant.Project, (await Names(db, [owner], http.RequestAborted)).GetValueOrDefault(owner), grant.View));
    }

    /// <summary>Replaces the definition (see the note in ProjectModels). Changing the owner is for the owner and workspace admins.</summary>
    private static async Task<IResult> Update(Guid id, ProjectInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEdit) return Results.Forbid();
        if (grant.Project.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (ProjectRules.Check(req) is { } invalid) return invalid;
        var project = grant.Project;
        if (req.OwnerUserId is { } owner && owner != project.OwnerUserId)
        {
            if (!grant.CanArchive) return Results.Forbid();
            if (!await ProjectRules.IsWorkerAsync(db, owner, project.WorkspaceId, http.RequestAborted)) return Http.Invalid("ownerUserId", "not_a_member");
            project.OwnerUserId = owner;
        }

        Fill(project, req, userId, clock.GetUtcNow());
        Audit.Add(db, http, clock, AuditActions.ProjectUpdated, project.WorkspaceId, userId, targetType: "project", targetId: id);
        if (await SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.NoContent();
    }

    private static Task<IResult> Archive(Guid id, HttpContext http, MonitorDb db, TimeProvider clock) =>
        SetStatus(id, ManagedStatuses.Archived, AuditActions.ProjectArchived, http, db, clock);

    private static Task<IResult> Restore(Guid id, HttpContext http, MonitorDb db, TimeProvider clock) =>
        SetStatus(id, ManagedStatuses.Active, AuditActions.ProjectRestored, http, db, clock);

    private static async Task<IResult> SetStatus(Guid id, string status, string action, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanArchive) return Results.Forbid();
        grant.Project.Status = status;
        grant.Project.UpdatedAt = clock.GetUtcNow();
        grant.Project.UpdatedBy = userId;
        Audit.Add(db, http, clock, action, grant.Project.WorkspaceId, userId, targetType: "project", targetId: id);
        if (await SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.NoContent();
    }

    private static async Task<IResult> Members(Guid id, HttpContext http, MonitorDb db)
    {
        var grant = await ProjectAccess.OfAsync(db, http.User.UserId(), id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        var rows = await (from m in db.ProjectMembers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.ProjectId == id
                          orderby u.DisplayName
                          select new ProjectMemberView(u.Id, u.DisplayName, u.Email, m.Role, m.AddedAt)).ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutMember(Guid id, Guid userId, ProjectMemberInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var actor = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, actor, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEdit) return Results.Forbid();
        if (req.Role is null || !ProjectRoles.All.Contains(req.Role)) return Http.Invalid("role", "invalid_role");
        if (await Access.MemberAsync(db, userId, grant.Project.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.Invalid("userId", "not_a_member");

        var row = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == id && m.UserId == userId, http.RequestAborted);
        var now = clock.GetUtcNow();
        if (row is null) db.ProjectMembers.Add(new ProjectMember { ProjectId = id, UserId = userId, Role = req.Role, AddedBy = actor, AddedAt = now });
        else row.Role = req.Role;
        Audit.Add(db, http, clock, AuditActions.ProjectMemberChanged, grant.Project.WorkspaceId, actor, targetType: "project", targetId: id,
            detail: new { userId, role = req.Role });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMember(Guid id, Guid userId, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var actor = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, actor, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEdit) return Results.Forbid();
        var row = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == id && m.UserId == userId, http.RequestAborted);
        if (row is null) return Http.NotFound();
        db.ProjectMembers.Remove(row);
        Audit.Add(db, http, clock, AuditActions.ProjectMemberRemoved, grant.Project.WorkspaceId, actor, targetType: "project", targetId: id,
            detail: new { userId });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static void Fill(ManagedProject p, ProjectInput i, Guid userId, DateTimeOffset now)
    {
        p.Name = i.Name!.Trim();
        p.NameSearch = SearchText.Normalize(p.Name);
        p.Purpose = ProjectRules.Clean(i.Purpose);
        p.Sponsor = ProjectRules.Clean(i.Sponsor);
        p.Stakeholders = ProjectJson.Pack((i.Stakeholders ?? []).Select(s => new Stakeholder(s.Name!.Trim(), ProjectRules.Clean(s.Role))).ToList());
        p.BudgetAmount = i.BudgetAmount;
        p.BudgetCurrency = i.BudgetAmount is null ? null : i.BudgetCurrency!.Trim();
        p.StartDate = i.StartDate;
        p.EndDate = i.EndDate;
        if (i.ItemTypes is not null) p.ItemTypes = [.. i.ItemTypes];
        p.PriorityScale = i.PriorityScale ?? p.PriorityScale;
        p.EstimationUnit = i.EstimationUnit ?? p.EstimationUnit;
        p.SprintLengthDays = i.SprintLengthDays ?? p.SprintLengthDays;
        p.DefinitionOfReady = [.. ProjectRules.CleanList(i.DefinitionOfReady)];
        p.DefinitionOfDone = [.. ProjectRules.CleanList(i.DefinitionOfDone)];
        p.UpdatedBy = userId;
        p.UpdatedAt = now;
    }

    private static async Task<Dictionary<Guid, string>> Names(MonitorDb db, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>Saves; a second active project of the same name answers <c>name_taken</c> instead of failing.</summary>
    internal static async Task<IResult?> SaveUniqueAsync(MonitorDb db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Http.Invalid("name", "name_taken");
        }
    }
}
