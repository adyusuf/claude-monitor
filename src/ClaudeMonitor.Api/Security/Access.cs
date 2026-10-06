using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Security;

/// <summary>
/// Workspace authorisation, checked in the API on every call (global #6). Fail-closed: no membership row, a removed
/// member, an unknown role or a role below the minimum all answer "no". Callers turn "no" into 404, so a workspace
/// one cannot see is indistinguishable from one that does not exist.
/// </summary>
public static class Access
{
    public static async Task<WorkspaceMember?> MemberAsync(
        MonitorDb db, Guid userId, Guid workspaceId, string minRole, CancellationToken ct)
    {
        var member = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null)
            .Join(db.Workspaces.Where(w => w.Status == WorkspaceStatuses.Active), m => m.WorkspaceId, w => w.Id, (m, _) => m)
            .FirstOrDefaultAsync(ct);
        return member is not null && Roles.Rank(member.Role) >= Roles.Rank(minRole) && Roles.Rank(minRole) > 0
            ? member
            : null;
    }

    /// <summary>A session the user may read (any role), with its workspace membership.</summary>
    public static async Task<(HarnessSession Session, WorkspaceMember Member)?> SessionAsync(
        MonitorDb db, Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await db.HarnessSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return null;
        }

        var member = await MemberAsync(db, userId, session.WorkspaceId, Roles.Viewer, ct);
        return member is null ? null : (session, member);
    }

    /// <summary>Only the session's owner (the user whose agent runs it) may command it or answer its permissions.</summary>
    public static async Task<bool> OwnsSessionAsync(MonitorDb db, Guid userId, HarnessSession session, CancellationToken ct) =>
        await db.Agents.AsNoTracking().AnyAsync(a => a.Id == session.AgentId && a.UserId == userId, ct);
}
