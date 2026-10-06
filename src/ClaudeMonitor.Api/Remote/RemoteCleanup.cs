using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// When an agent leaves a workspace (revoked, moved) or a member is removed, its remote work ends with it (ADR-0005):
/// grants revoked, jobs retired, open alerts resolved, open runs cancelled. Runs are cancelled and published at once;
/// the rest is part of the caller's unit of work only where it says so.
/// </summary>
public static class RemoteCleanup
{
    public static async Task ForAgentAsync(MonitorDb db, Broker broker, Guid agentId, Guid? actor, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.MachineGrants
            .Where(g => (g.TargetAgentId == agentId || g.GranteeAgentId == agentId)
                        && (g.Status == GrantStatuses.Active || g.Status == GrantStatuses.Requested))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Revoked).SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokedBy, actor), ct);
        await db.MachineJobs
            .Where(j => j.TargetAgentId == agentId && (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Retired).SetProperty(j => j.RetiredAt, now), ct);
        await db.MachineAlerts.Where(a => a.AgentId == agentId && a.State == AlertStates.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AlertStates.Resolved).SetProperty(a => a.ResolvedAt, now), ct);
        await RunDecisions.CancelOpenAsync(db, broker, now, null, agentId, null, ct);
    }

    public static async Task ForMemberAsync(MonitorDb db, Broker broker, Guid workspaceId, Guid userId, Guid? actor, DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.MachineGrants
            .Where(g => g.WorkspaceId == workspaceId && (g.OwnerUserId == userId || g.GranteeUserId == userId)
                        && (g.Status == GrantStatuses.Active || g.Status == GrantStatuses.Requested))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Revoked).SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokedBy, actor), ct);
        await db.MachineJobs
            .Where(j => j.WorkspaceId == workspaceId && j.OwnerUserId == userId
                        && (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Retired).SetProperty(j => j.RetiredAt, now), ct);
        await RunDecisions.CancelOpenAsync(db, broker, now, workspaceId, null, userId, ct);
    }
}
