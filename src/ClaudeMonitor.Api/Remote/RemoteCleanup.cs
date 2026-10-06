using System.Linq.Expressions;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// When an agent leaves a workspace (revoked, moved) or a member is removed, its remote work ends with it (ADR-0005):
/// grants revoked, jobs retired, open alerts resolved, open runs cancelled. Runs are cancelled and published at once;
/// the rest is part of the caller's unit of work only where it says so. Every cascade first locks all the rows it will change
/// in the one global order (<see cref="RemoteLocks"/>: jobs, grants, alerts, runs, each by id), whatever number of agents and
/// members it covers, and only then updates them, so two cascades over overlapping rows cannot wait on each other.
/// </summary>
public static class RemoteCleanup
{
    public static Task ForAgentAsync(MonitorDb db, Broker broker, Guid agentId, Guid? actor, DateTimeOffset now, CancellationToken ct,
        List<Action>? after = null) =>
        ForAsync(db, broker, null, null, [agentId], actor, now, after, ct);

    /// <summary>
    /// A removed member's remote work and that of the agents they had in the workspace (<paramref name="agentIds"/>), as one
    /// cascade: a cascade per agent would lock one agent's rows before the next agent's jobs, against the global order.
    /// </summary>
    public static Task ForMemberAsync(MonitorDb db, Broker broker, Guid workspaceId, Guid userId, Guid? actor, DateTimeOffset now,
        CancellationToken ct, List<Action>? after = null, IReadOnlyCollection<Guid>? agentIds = null) =>
        ForAsync(db, broker, workspaceId, userId, agentIds ?? [], actor, now, after, ct);

    /// <summary>
    /// The agents' rows (as target or grantee, requester or target of a run) and, when there is a member, theirs in the
    /// workspace. Guid.Empty stands for "no member": it matches no row, so the member's half of each filter selects nothing.
    /// </summary>
    private static async Task ForAsync(MonitorDb db, Broker broker, Guid? workspaceId, Guid? userId, IReadOnlyCollection<Guid> agentIds,
        Guid? actor, DateTimeOffset now, List<Action>? after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var agents = agentIds.ToArray();
        var ws = workspaceId ?? Guid.Empty;
        var user = userId ?? Guid.Empty;
        Expression<Func<MachineJob, bool>> jobs = j => (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed)
            && (agents.Contains(j.TargetAgentId) || (j.WorkspaceId == ws && j.OwnerUserId == user));
        Expression<Func<MachineGrant, bool>> grants = g => (g.Status == GrantStatuses.Active || g.Status == GrantStatuses.Requested)
            && (agents.Contains(g.TargetAgentId) || (g.GranteeAgentId != null && agents.Contains(g.GranteeAgentId.Value))
                || (g.WorkspaceId == ws && (g.OwnerUserId == user || g.GranteeUserId == user)));
        Expression<Func<MachineAlert, bool>> alerts = a => agents.Contains(a.AgentId) && a.State == AlertStates.Open;
        Expression<Func<RemoteRun, bool>> runs = r => agents.Contains(r.TargetAgentId) || agents.Contains(r.RequesterAgentId)
            || (r.WorkspaceId == ws && (r.TargetUserId == user || r.RequesterUserId == user));

        await RemoteLocks.JobsAsync(db, jobs, ct);
        await RemoteLocks.GrantsAsync(db, grants, ct);
        await RemoteLocks.AlertsAsync(db, alerts, ct);
        await db.MachineJobs.Where(jobs)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Retired).SetProperty(j => j.RetiredAt, now), ct);
        await db.MachineGrants.Where(grants)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Revoked).SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokedBy, actor), ct);
        await db.MachineAlerts.Where(alerts)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AlertStates.Resolved).SetProperty(a => a.ResolvedAt, now), ct);
        await RunDecisions.CancelOpenAsync(db, broker, now, runs, ct, after);
    }
}
