using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Projects;
using Podium.Api.Security;
using Podium.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>Applications of a managed project (ADR-0011). Rights come from <see cref="ProjectAccess"/>; an application is archived, never deleted.</summary>
public static class ApplicationEndpoints
{
    public const int PerProjectMax = 100;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/projects/{id:guid}/applications", List);
        g.MapPost("/projects/{id:guid}/applications", Create);
        g.MapGet("/applications/{id:guid}", Get);
        g.MapPut("/applications/{id:guid}", Update);
        g.MapDelete("/applications/{id:guid}", Archive);
        g.MapPost("/applications/{id:guid}/restore", Restore);
    }

    private static async Task<IResult> List(Guid id, string? status, string? cursor, int? limit, HttpContext http, PodiumDb db, ApiConfig config)
    {
        if (await ProjectAccess.OfAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.Applications.AsNoTracking().Where(a => a.ProjectId == id);
        query = status switch
        {
            "all" => query,
            ManagedStatuses.Archived => query.Where(a => a.Status == ManagedStatuses.Archived),
            _ => query.Where(a => a.Status == ManagedStatuses.Active),
        };
        if (Cursor.Parse(cursor) is { } after)
        {
            query = query.Where(a => a.CreatedAt < after.At || (a.CreatedAt == after.At && a.Id.CompareTo(after.Id) < 0));
        }

        var page = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(http.RequestAborted);
        var shown = page.Take(take).ToList();
        var next = page.Count > take ? Cursor.Of(shown[^1].CreatedAt, shown[^1].Id) : null;
        return Results.Ok(new Page<ApplicationSummary>(shown.Select(ProjectViews.Summary).ToList(), next));
    }

    private static async Task<IResult> Create(Guid id, ApplicationInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, id, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEditApplications) return Results.Forbid();
        if (grant.Project.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (ProjectRules.Check(req) is { } invalid) return invalid;
        if (await TechOwnerInvalidAsync(db, req, grant.Project.WorkspaceId, http.RequestAborted)) return Http.Invalid("techOwnerUserId", "not_a_member");
        if (await db.Applications.CountAsync(a => a.ProjectId == id && a.Status == ManagedStatuses.Active, http.RequestAborted) >= PerProjectMax)
        {
            return Http.Invalid("name", "limit_reached");
        }

        var now = clock.GetUtcNow();
        var app = new ManagedApplication { WorkspaceId = grant.Project.WorkspaceId, ProjectId = id, CreatedBy = userId, CreatedAt = now };
        Fill(app, req, userId, now);
        db.Applications.Add(app);
        Audit.Add(db, http, clock, AuditActions.ApplicationCreated, app.WorkspaceId, userId, targetType: "application", targetId: app.Id);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.Created($"/api/applications/{app.Id}", ProjectViews.View(app, await TechOwnerNameAsync(db, app, http.RequestAborted), true));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, PodiumDb db)
    {
        var found = await ProjectAccess.ApplicationAsync(db, http.User.UserId(), id, http.RequestAborted);
        if (found is not { } f) return Http.NotFound();
        return Results.Ok(ProjectViews.View(f.App, await TechOwnerNameAsync(db, f.App, http.RequestAborted), f.Grant.CanEditApplications));
    }

    /// <summary>Replaces the definition (see the note in ProjectModels).</summary>
    private static async Task<IResult> Update(Guid id, ApplicationInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var found = await ProjectAccess.ApplicationAsync(db, userId, id, http.RequestAborted);
        if (found is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (f.App.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (ProjectRules.Check(req) is { } invalid) return invalid;
        if (await TechOwnerInvalidAsync(db, req, f.App.WorkspaceId, http.RequestAborted)) return Http.Invalid("techOwnerUserId", "not_a_member");
        Fill(f.App, req, userId, clock.GetUtcNow());
        Audit.Add(db, http, clock, AuditActions.ApplicationUpdated, f.App.WorkspaceId, userId, targetType: "application", targetId: id);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.NoContent();
    }

    private static Task<IResult> Archive(Guid id, HttpContext http, PodiumDb db, TimeProvider clock) =>
        SetStatus(id, ManagedStatuses.Archived, AuditActions.ApplicationArchived, http, db, clock);

    private static Task<IResult> Restore(Guid id, HttpContext http, PodiumDb db, TimeProvider clock) =>
        SetStatus(id, ManagedStatuses.Active, AuditActions.ApplicationRestored, http, db, clock);

    private static async Task<IResult> SetStatus(Guid id, string status, string action, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var found = await ProjectAccess.ApplicationAsync(db, userId, id, http.RequestAborted);
        if (found is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        f.App.Status = status;
        f.App.UpdatedAt = clock.GetUtcNow();
        f.App.UpdatedBy = userId;
        Audit.Add(db, http, clock, action, f.App.WorkspaceId, userId, targetType: "application", targetId: id);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.NoContent();
    }

    private static void Fill(ManagedApplication a, ApplicationInput i, Guid userId, DateTimeOffset now)
    {
        a.Name = i.Name!.Trim();
        a.NameSearch = SearchText.Normalize(a.Name);
        a.Description = ProjectRules.Clean(i.Description);
        a.Kind = i.Kind ?? a.Kind;
        a.RepoUrl = ProjectRules.Clean(i.RepoUrl);
        a.Environments = ProjectJson.Pack((i.Environments ?? []).Select(e => new EnvironmentEntry(e.Name, ProjectRules.Clean(e.Url))).ToList());
        a.TechStack = [.. ProjectRules.CleanList(i.TechStack)];
        a.VersioningScheme = i.VersioningScheme ?? a.VersioningScheme;
        a.TechOwnerUserId = i.TechOwnerUserId;
        a.NonFunctionalTargets = ProjectJson.Pack((i.NonFunctionalTargets ?? []).Select(t => new TargetEntry(t.Kind, t.Target!.Trim())).ToList());
        a.UpdatedBy = userId;
        a.UpdatedAt = now;
    }

    private static async Task<bool> TechOwnerInvalidAsync(PodiumDb db, ApplicationInput i, Guid workspaceId, CancellationToken ct) =>
        i.TechOwnerUserId is { } owner && !await ProjectRules.IsWorkerAsync(db, owner, workspaceId, ct);

    private static async Task<string?> TechOwnerNameAsync(PodiumDb db, ManagedApplication a, CancellationToken ct) =>
        a.TechOwnerUserId is { } owner ? await db.Users.AsNoTracking().Where(u => u.Id == owner).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
}
