using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>
/// The role a standing approval needs (F-163), as run creation and run decisions need it: a member or above. A viewer cannot make a rule and no
/// rule of theirs matches a request; a member whose role drops below member loses their live rules in that workspace at once, each revoke audited.
/// A viewer still answers the permission requests of their own sessions one at a time.
/// </summary>
public static class ApprovalRuleFloor
{
    public const string MinRole = Roles.Member;

    /// <summary>The audit detail's reason for a rule revoked by a role change.</summary>
    public const string RoleChanged = "role_changed";

    /// <summary>True when the user holds at least <see cref="MinRole"/> in the (active) workspace now.</summary>
    public static async Task<bool> HoldsAsync(PodiumDb db, Guid userId, Guid workspaceId, CancellationToken ct) =>
        await Access.MemberAsync(db, userId, workspaceId, MinRole, ct) is not null;

    /// <summary>
    /// For a role change to <paramref name="role"/>: below <see cref="MinRole"/> the member's live rules in the workspace are revoked, with an audit
    /// row each in the caller's unit of work (saved with the role change); at or above it nothing changes. Returns how many were revoked.
    /// </summary>
    public static async Task<int> ForRoleChangeAsync(PodiumDb db, HttpContext http, TimeProvider clock, Guid workspaceId, Guid userId, string role,
        Guid actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(role);
        if (Roles.Rank(role) >= Roles.Rank(MinRole)) return 0;
        var now = clock.GetUtcNow();
        var live = await db.ApprovalRules.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.UserId == userId && r.RevokedAt == null)
            .OrderBy(r => r.Id).Select(r => new { r.Id, r.ToolName, r.Duration }).ToListAsync(ct);
        var ids = live.Select(r => r.Id).ToList();
        await db.ApprovalRules.Where(r => ids.Contains(r.Id) && r.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
        foreach (var rule in live)
        {
            Audit.Add(db, http, clock, AuditActions.ApprovalRuleRevoked, workspaceId, actor, targetType: "approval_rule", targetId: rule.Id,
                detail: new { rule.ToolName, rule.Duration, reason = RoleChanged, user = userId });
        }

        return live.Count;
    }
}
