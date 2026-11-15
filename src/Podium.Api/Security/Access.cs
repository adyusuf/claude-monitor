using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Security;

/// <summary>
/// Workspace authorisation, checked in the API on every call (global #6). Fail-closed: no membership row, a removed
/// member, an unknown role or a role below the minimum all answer "no". Callers turn "no" into 404, so a workspace
/// one cannot see is indistinguishable from one that does not exist.
/// </summary>
public static class Access
{
    public static async Task<WorkspaceMember?> MemberAsync(
        PodiumDb db, Guid userId, Guid workspaceId, string minRole, CancellationToken ct)
    {
        var member = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null)
            .Join(db.Workspaces.Where(w => w.Status == WorkspaceStatuses.Active), m => m.WorkspaceId, w => w.Id, (m, _) => m)
            .FirstOrDefaultAsync(ct);
        return member is not null && Roles.Rank(member.Role) >= Roles.Rank(minRole) && Roles.Rank(minRole) > 0
            ? member
            : null;
    }

    /// <summary>
    /// Whether a reader of a workspace's live stream may still read it: its login is not signed out or expired, the user is active and still a
    /// member. Asked again while the stream is open, because the stream outlives the request that opened it.
    /// </summary>
    public static async Task<bool> StillReadsAsync(PodiumDb db, TimeProvider clock, Guid userId, Guid? loginSessionId, Guid workspaceId,
        CancellationToken ct)
    {
        if (loginSessionId is not { } loginId)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var signedIn = await (from s in db.LoginSessions.AsNoTracking()
                              join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                              where s.Id == loginId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now
                                    && u.Status == UserStatuses.Active
                              select s.Id).AnyAsync(ct);
        return signedIn && await MemberAsync(db, userId, workspaceId, Roles.Viewer, ct) is not null;
    }

    /// <summary>A session the user may read (any role), with its workspace membership.</summary>
    public static async Task<(HarnessSession Session, WorkspaceMember Member)?> SessionAsync(
        PodiumDb db, Guid userId, Guid sessionId, CancellationToken ct)
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
    public static async Task<bool> OwnsSessionAsync(PodiumDb db, Guid userId, HarnessSession session, CancellationToken ct) =>
        await db.Agents.AsNoTracking().AnyAsync(a => a.Id == session.AgentId && a.UserId == userId, ct);
}
