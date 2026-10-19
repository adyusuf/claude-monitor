using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Projects;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// The components and modules of an application and its release calendar (ADR-0011). Both lists are small by design: the caps below
/// stand in for paging. Rights are those of the application's project.
/// </summary>
public static class ApplicationPartEndpoints
{
    public const int ComponentsMax = 200;
    public const int ReleasesMax = 200;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/applications/{id:guid}/components", Components);
        g.MapPost("/applications/{id:guid}/components", AddComponent);
        g.MapPut("/components/{id:guid}", PutComponent);
        g.MapDelete("/components/{id:guid}", ArchiveComponent);
        g.MapGet("/applications/{id:guid}/releases", Releases);
        g.MapPost("/applications/{id:guid}/releases", AddRelease);
        g.MapPut("/releases/{id:guid}", PutRelease);
        g.MapDelete("/releases/{id:guid}", CancelRelease);
    }

    private static async Task<IResult> Components(Guid id, HttpContext http, MonitorDb db)
    {
        if (await ProjectAccess.ApplicationAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await db.ApplicationComponents.AsNoTracking().Where(c => c.ApplicationId == id && c.Status == ManagedStatuses.Active)
            .OrderBy(c => c.NameSearch).Take(ComponentsMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(ProjectViews.View).ToList());
    }

    private static async Task<IResult> AddComponent(Guid id, ComponentInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.ApplicationAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (f.App.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (Invalid(req) is { } invalid) return invalid;
        if (await db.ApplicationComponents.CountAsync(c => c.ApplicationId == id && c.Status == ManagedStatuses.Active, http.RequestAborted) >= ComponentsMax)
        {
            return Http.Invalid("name", "limit_reached");
        }

        var row = new ApplicationComponent { WorkspaceId = f.App.WorkspaceId, ApplicationId = id, CreatedAt = clock.GetUtcNow() };
        Fill(row, req);
        db.ApplicationComponents.Add(row);
        Audit.Add(db, http, clock, AuditActions.ComponentChanged, row.WorkspaceId, userId, targetType: "application", targetId: id);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.Created($"/api/components/{row.Id}", ProjectViews.View(row));
    }

    private static async Task<IResult> PutComponent(Guid id, ComponentInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.ApplicationComponents.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.ApplicationAsync(db, userId, row.ApplicationId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (row.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (Invalid(req) is { } invalid) return invalid;
        Fill(row, req);
        Audit.Add(db, http, clock, AuditActions.ComponentChanged, row.WorkspaceId, userId, targetType: "application", targetId: row.ApplicationId);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.NoContent();
    }

    private static async Task<IResult> ArchiveComponent(Guid id, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.ApplicationComponents.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.ApplicationAsync(db, userId, row.ApplicationId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        row.Status = ManagedStatuses.Archived;
        Audit.Add(db, http, clock, AuditActions.ComponentChanged, row.WorkspaceId, userId, targetType: "application", targetId: row.ApplicationId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Releases(Guid id, HttpContext http, MonitorDb db)
    {
        if (await ProjectAccess.ApplicationAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await db.ApplicationReleases.AsNoTracking().Where(r => r.ApplicationId == id && r.Status != ReleaseStatuses.Cancelled)
            .OrderBy(r => r.PlannedDate == null).ThenBy(r => r.PlannedDate).ThenBy(r => r.NameSearch).Take(ReleasesMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(ProjectViews.View).ToList());
    }

    private static async Task<IResult> AddRelease(Guid id, ReleaseInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.ApplicationAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (f.App.Status != ManagedStatuses.Active) return Http.Invalid("status", "archived");
        if (Invalid(req) is { } invalid) return invalid;
        if (await db.ApplicationReleases.CountAsync(r => r.ApplicationId == id && r.Status != ReleaseStatuses.Cancelled, http.RequestAborted) >= ReleasesMax)
        {
            return Http.Invalid("name", "limit_reached");
        }

        var row = new ApplicationRelease { WorkspaceId = f.App.WorkspaceId, ApplicationId = id, CreatedAt = clock.GetUtcNow() };
        Fill(row, req);
        db.ApplicationReleases.Add(row);
        Audit.Add(db, http, clock, AuditActions.ReleaseChanged, row.WorkspaceId, userId, targetType: "application", targetId: id);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.Created($"/api/releases/{row.Id}", ProjectViews.View(row));
    }

    private static async Task<IResult> PutRelease(Guid id, ReleaseInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.ApplicationReleases.FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.ApplicationAsync(db, userId, row.ApplicationId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (row.Status == ReleaseStatuses.Cancelled) return Http.Invalid("status", "cancelled");
        if (Invalid(req) is { } invalid) return invalid;
        Fill(row, req);
        Audit.Add(db, http, clock, AuditActions.ReleaseChanged, row.WorkspaceId, userId, targetType: "application", targetId: row.ApplicationId);
        if (await ProjectEndpoints.SaveUniqueAsync(db, http.RequestAborted) is { } taken) return taken;
        return Results.NoContent();
    }

    /// <summary>A planned release is cancelled; one that went out is history and stays.</summary>
    private static async Task<IResult> CancelRelease(Guid id, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.ApplicationReleases.FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.ApplicationAsync(db, userId, row.ApplicationId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (row.Status == ReleaseStatuses.Released) return Http.Invalid("status", "released");
        row.Status = ReleaseStatuses.Cancelled;
        Audit.Add(db, http, clock, AuditActions.ReleaseChanged, row.WorkspaceId, userId, targetType: "application", targetId: row.ApplicationId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static IResult? Invalid(ComponentInput i) =>
        !Http.IsName(i.Name, ProjectRules.NameMax) ? Http.Invalid("name", "invalid_name")
        : i.Kind is { } k && !ComponentKinds.All.Contains(k) ? Http.Invalid("kind", "invalid_value")
        : null;

    private static IResult? Invalid(ReleaseInput i) =>
        !Http.IsName(i.Name, ProjectRules.NameMax) ? Http.Invalid("name", "invalid_name")
        : i.Status is { } s && !ReleaseStatuses.All.Contains(s) ? Http.Invalid("status", "invalid_value")
        : null;

    private static void Fill(ApplicationComponent row, ComponentInput i)
    {
        row.Name = i.Name!.Trim();
        row.NameSearch = SearchText.Normalize(row.Name);
        row.Kind = i.Kind ?? row.Kind;
    }

    private static void Fill(ApplicationRelease row, ReleaseInput i)
    {
        row.Name = i.Name!.Trim();
        row.NameSearch = SearchText.Normalize(row.Name);
        row.PlannedDate = i.PlannedDate;
        row.Status = i.Status ?? row.Status;
    }
}
