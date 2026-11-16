using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// A member who still has issues assigned is not removed, and not lowered below Member (a viewer cannot be an assignee): F-102 (B). Archived
/// issues count too, since a restore brings them back. The answer is 409 <see cref="HasIssues"/> with <c>count</c> (not archived) and
/// <c>archivedCount</c>; the issues are reassigned first.
/// </summary>
public static class MemberIssueGuard
{
    public const string HasIssues = "member_has_issues";

    /// <summary>The refusal when issues of the workspace are assigned to <paramref name="userId"/>, or null when none are.</summary>
    public static async Task<IResult?> RefuseAsync(PodiumDb db, Guid workspaceId, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var assigned = db.Issues.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.AssigneeUserId == userId);
        var archived = await assigned.CountAsync(i => i.ArchivedAt != null, ct);
        var open = await assigned.CountAsync(i => i.ArchivedAt == null, ct);
        if (open == 0 && archived == 0) return null;
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: HasIssues,
            extensions: new Dictionary<string, object?> { ["count"] = open, ["archivedCount"] = archived });
    }

    /// <summary>True when a change from <paramref name="from"/> to <paramref name="to"/> takes the member below Member (a viewer is never an assignee).</summary>
    public static bool LowersBelowMember(string from, string to) =>
        Roles.Rank(to) < Roles.Rank(Roles.Member) && Roles.Rank(from) >= Roles.Rank(Roles.Member);
}
