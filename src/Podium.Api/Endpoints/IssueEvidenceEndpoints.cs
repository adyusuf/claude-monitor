using Podium.Api.Data;
using Podium.Api.Projects;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>The test scenarios of an issue with their latest result, and its pull request and commit links (ADR-0011 §4).</summary>
public static class IssueEvidenceEndpoints
{
    public const int TestCasesMax = 100;
    public const int CodeLinksMax = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/issues/{id:guid}/test-cases", TestCases);
        g.MapPost("/issues/{id:guid}/test-cases", AddTestCase);
        g.MapPut("/test-cases/{id:guid}", PutTestCase);
        g.MapDelete("/test-cases/{id:guid}", RemoveTestCase);
        g.MapPost("/test-cases/{id:guid}/result", SetResult);
        g.MapGet("/issues/{id:guid}/code-links", CodeLinks);
        g.MapPost("/issues/{id:guid}/code-links", AddCodeLink);
        g.MapDelete("/code-links/{id:guid}", RemoveCodeLink);
    }

    private static async Task<IResult> TestCases(Guid id, HttpContext http, PodiumDb db)
    {
        if (await ProjectAccess.IssueAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await db.IssueTestCases.AsNoTracking().Where(t => t.IssueId == id).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Take(TestCasesMax).ToListAsync(http.RequestAborted);
        var names = await IssueViews.NamesAsync(db, rows.Select(t => t.ResultBy), http.RequestAborted);
        return Results.Ok(rows.Select(t => IssueViews.View(t, t.ResultBy is { } who ? names.GetValueOrDefault(who) : null)).ToList());
    }

    private static async Task<IResult> AddTestCase(Guid id, TestCaseInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (Invalid(req) is { } invalid) return invalid;
        if (await db.IssueTestCases.CountAsync(t => t.IssueId == id, http.RequestAborted) >= TestCasesMax) return Http.Invalid("title", "limit_reached");
        var row = new IssueTestCase { WorkspaceId = f.Issue.WorkspaceId, IssueId = id, CreatedAt = clock.GetUtcNow() };
        Fill(row, req);
        db.IssueTestCases.Add(row);
        Audit.Add(db, http, clock, AuditActions.IssueTestCaseChanged, row.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/test-cases/{row.Id}", IssueViews.View(row, null));
    }

    private static async Task<IResult> PutTestCase(Guid id, TestCaseInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.IssueTestCases.FirstOrDefaultAsync(t => t.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.IssueAsync(db, userId, row.IssueId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (Invalid(req) is { } invalid) return invalid;
        Fill(row, req);
        Audit.Add(db, http, clock, AuditActions.IssueTestCaseChanged, row.WorkspaceId, userId, targetType: "issue", targetId: row.IssueId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTestCase(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.IssueTestCases.FirstOrDefaultAsync(t => t.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.IssueAsync(db, userId, row.IssueId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        db.IssueTestCases.Remove(row);
        Audit.Add(db, http, clock, AuditActions.IssueTestCaseChanged, row.WorkspaceId, userId, targetType: "issue", targetId: row.IssueId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> SetResult(Guid id, TestResultInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.IssueTestCases.FirstOrDefaultAsync(t => t.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.IssueAsync(db, userId, row.IssueId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (req.Result is null || !TestResults.All.Contains(req.Result)) return Http.Invalid("result", "invalid_value");
        if (req.Note is { } note && note.Trim().Length > ProjectRules.TextMax) return Http.Invalid("note", "too_long");
        row.Result = req.Result;
        row.ResultNote = ProjectRules.Clean(req.Note);
        row.ResultBy = userId;
        row.ResultAt = clock.GetUtcNow();
        Audit.Add(db, http, clock, AuditActions.IssueTestCaseChanged, row.WorkspaceId, userId, targetType: "issue", targetId: row.IssueId,
            detail: new { result = req.Result });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> CodeLinks(Guid id, HttpContext http, PodiumDb db)
    {
        if (await ProjectAccess.IssueAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await db.IssueCodeLinks.AsNoTracking().Where(c => c.IssueId == id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(CodeLinksMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(IssueViews.View).ToList());
    }

    private static async Task<IResult> AddCodeLink(Guid id, CodeLinkInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        if (req.Kind is null || !CodeLinkKinds.All.Contains(req.Kind)) return Http.Invalid("kind", "invalid_value");
        var url = req.Url?.Trim();
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !ProjectRules.WebLink(url)) return Http.Invalid("url", "invalid_url");
        if (req.Title is { } title && title.Trim().Length > ProjectRules.ShortMax) return Http.Invalid("title", "too_long");
        if (await db.IssueCodeLinks.AnyAsync(c => c.IssueId == id && c.Url == url, http.RequestAborted)) return Http.Invalid("url", "duplicate_link");
        if (await db.IssueCodeLinks.CountAsync(c => c.IssueId == id, http.RequestAborted) >= CodeLinksMax) return Http.Invalid("url", "limit_reached");
        var row = new IssueCodeLink
        {
            WorkspaceId = f.Issue.WorkspaceId,
            IssueId = id,
            Kind = req.Kind,
            Url = url,
            Title = ProjectRules.Clean(req.Title),
            Source = CodeLinkSources.Manual,
            CreatedBy = userId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.IssueCodeLinks.Add(row);
        Audit.Add(db, http, clock, AuditActions.IssueCodeLinkChanged, row.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/code-links/{row.Id}", IssueViews.View(row));
    }

    private static async Task<IResult> RemoveCodeLink(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var row = await db.IssueCodeLinks.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (row is null || await ProjectAccess.IssueAsync(db, userId, row.IssueId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        db.IssueCodeLinks.Remove(row);
        Audit.Add(db, http, clock, AuditActions.IssueCodeLinkChanged, row.WorkspaceId, userId, targetType: "issue", targetId: row.IssueId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static IResult? Invalid(TestCaseInput i) =>
        !Http.IsName(i.Title, IssueRules.TitleMax) ? Http.Invalid("title", "invalid_name")
        : i.Steps is { } s && s.Trim().Length > IssueRules.StepsMax ? Http.Invalid("steps", "too_long")
        : i.Expected is { } e && e.Trim().Length > IssueRules.ResultMax ? Http.Invalid("expected", "too_long")
        : null;

    private static void Fill(IssueTestCase row, TestCaseInput i)
    {
        row.Title = i.Title!.Trim();
        row.Steps = ProjectRules.Clean(i.Steps);
        row.Expected = ProjectRules.Clean(i.Expected);
    }
}
