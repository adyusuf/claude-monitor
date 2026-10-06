using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// A grant as the web shows it to the target's owner and the grantee. RequestedByHostname names the machine whose
/// Claude asked; Reason is what that Claude wrote (untrusted). CanDecide: the owner may approve or deny it now.
/// CanRevoke: the owner or the grantee may end it.
/// </summary>
public sealed record WebGrantView(Guid Id, Guid TargetAgentId, Guid OwnerUserId, Guid GranteeUserId, string GranteeName,
    Guid? GranteeAgentId, Guid? RequestedByAgentId, string? RequestedByHostname, IReadOnlyList<string> Template, string Cwd,
    int MaxTimeoutSeconds, string? Reason, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt, DateTimeOffset? RevokedAt, int UseCount, DateTimeOffset? LastUsedAt, bool CanDecide, bool CanRevoke);

public static class GrantQueries
{
    public const int AgentListMax = 100;
    public static readonly TimeSpan AgentListWindow = TimeSpan.FromDays(90);

    public static GrantView View(MachineGrant g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return new GrantView(g.Id, g.TargetAgentId, g.Template ?? [], g.Cwd ?? "", g.MaxTimeoutSeconds, g.Status, g.ExpiresAt,
            g.UseCount, g.LastUsedAt);
    }

    /// <summary>The grants a user holds, newest first, optionally on one target; only the last 90 days.</summary>
    public static async Task<List<GrantView>> ForGranteeAsync(MonitorDb db, Guid userId, Guid workspaceId, Guid? targetAgentId,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var since = now - AgentListWindow;
        var rows = await db.MachineGrants.AsNoTracking()
            .Where(g => g.GranteeUserId == userId && g.WorkspaceId == workspaceId && g.CreatedAt > since
                        && (targetAgentId == null || g.TargetAgentId == targetAgentId))
            .OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id).Take(AgentListMax).ToListAsync(ct);
        return rows.Select(View).ToList();
    }

    /// <summary>The grants of one target for the web, newest first, key-paged. The owner sees every grant; anyone else only theirs as grantee.</summary>
    public static async Task<List<WebGrantView>> ForWebAsync(MonitorDb db, Guid targetAgentId, Guid userId, bool ownerSeesAll,
        Guid? grantId, DateTimeOffset? before, int limit, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from g in db.MachineGrants.AsNoTracking()
                          join u in db.Users.AsNoTracking() on g.GranteeUserId equals u.Id
                          where g.TargetAgentId == targetAgentId
                                && (ownerSeesAll || g.GranteeUserId == userId)
                                && (grantId == null || g.Id == grantId)
                                && (before == null || g.CreatedAt < before)
                          orderby g.CreatedAt descending, g.Id descending
                          select new
                          {
                              g,
                              Grantee = u.DisplayName,
                              Host = (from ra in db.Agents join rm in db.Machines on ra.MachineId equals rm.Id
                                      where ra.Id == g.RequestedByAgentId select rm.Hostname).FirstOrDefault(),
                          }).Take(limit).ToListAsync(ct);
        return rows.Select(x =>
        {
            var g = x.g;
            var owner = g.OwnerUserId == userId;
            var requested = g.Status == GrantStatuses.Requested;
            return new WebGrantView(g.Id, g.TargetAgentId, g.OwnerUserId, g.GranteeUserId, x.Grantee, g.GranteeAgentId,
                g.RequestedByAgentId, x.Host, g.Template ?? [], g.Cwd ?? "", g.MaxTimeoutSeconds, g.Reason, g.Status, g.CreatedAt,
                g.ExpiresAt, g.DecidedAt, g.RevokedAt, g.UseCount, g.LastUsedAt,
                CanDecide: owner && requested && g.ExpiresAt > now,
                CanRevoke: (owner || g.GranteeUserId == userId) && (requested || g.Status == GrantStatuses.Active));
        }).ToList();
    }
}
