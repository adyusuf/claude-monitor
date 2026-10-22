using Podium.Api.Data;
using Podium.Api.Projects;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>"Blocks" and "blocked by" between issues of one project (ADR-0011 §4). One row per pair; a loop of any length is refused.</summary>
public static class IssueLinkEndpoints
{
    public const int PerIssueMax = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapPost("/issues/{id:guid}/links", Add);
        g.MapDelete("/issue-links/{id:guid}", Remove);
    }

    private static async Task<IResult> Add(Guid id, LinkInput req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await ProjectAccess.IssueAsync(db, userId, id, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        var issue = f.Issue;
        if (issue.ArchivedAt is not null) return Http.Invalid("status", "archived");
        if (req.Kind is null || !IssueLinkKinds.All.Contains(req.Kind)) return Http.Invalid("kind", "invalid_value");
        if (req.OtherIssueId is not { } otherId) return Http.Invalid("otherIssueId", "invalid_issue");
        if (otherId == id) return Http.Invalid("otherIssueId", "self_link");
        if (!await db.Issues.AnyAsync(i => i.Id == otherId && i.ProjectId == issue.ProjectId && i.ArchivedAt == null, http.RequestAborted))
        {
            return Http.Invalid("otherIssueId", "invalid_issue");
        }

        var (blocker, blocked) = req.Kind == IssueLinkKinds.Blocks ? (id, otherId) : (otherId, id);
        if (await db.IssueLinks.CountAsync(l => l.BlockerIssueId == id || l.BlockedIssueId == id, http.RequestAborted) >= PerIssueMax)
        {
            return Http.Invalid("otherIssueId", "limit_reached");
        }

        var pairs = await db.IssueLinks.AsNoTracking().Where(l => l.ProjectId == issue.ProjectId)
            .Select(l => new { l.BlockerIssueId, l.BlockedIssueId }).ToListAsync(http.RequestAborted);
        if (pairs.Any(p => p.BlockerIssueId == blocker && p.BlockedIssueId == blocked)) return Http.Invalid("otherIssueId", "duplicate_link");
        if (Reaches(pairs.Select(p => (p.BlockerIssueId, p.BlockedIssueId)), from: blocked, to: blocker)) return Http.Invalid("otherIssueId", "cycle");

        db.IssueLinks.Add(new IssueLink
        {
            WorkspaceId = issue.WorkspaceId,
            ProjectId = issue.ProjectId,
            BlockerIssueId = blocker,
            BlockedIssueId = blocked,
            CreatedBy = userId,
            CreatedAt = clock.GetUtcNow(),
        });
        Audit.Add(db, http, clock, AuditActions.IssueLinkChanged, issue.WorkspaceId, userId, targetType: "issue", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Remove(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var link = await db.IssueLinks.FirstOrDefaultAsync(l => l.Id == id, http.RequestAborted);
        if (link is null || await ProjectAccess.IssueAsync(db, userId, link.BlockerIssueId, http.RequestAborted) is not { } f) return Http.NotFound();
        if (!f.Grant.CanEditApplications) return Results.Forbid();
        db.IssueLinks.Remove(link);
        Audit.Add(db, http, clock, AuditActions.IssueLinkChanged, link.WorkspaceId, userId, targetType: "issue", targetId: link.BlockerIssueId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>True when "blocks" edges lead from one issue to the other (a path of any length), which a new edge back would turn into a loop.</summary>
    internal static bool Reaches(IEnumerable<(Guid Blocker, Guid Blocked)> edges, Guid from, Guid to)
    {
        var next = edges.ToLookup(e => e.Blocker, e => e.Blocked);
        var seen = new HashSet<Guid> { from };
        var queue = new Queue<Guid>([from]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == to) return true;
            foreach (var step in next[current])
            {
                if (seen.Add(step)) queue.Enqueue(step);
            }
        }

        return false;
    }
}
