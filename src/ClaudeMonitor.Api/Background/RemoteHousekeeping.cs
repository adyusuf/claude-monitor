using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Background;

/// <summary>
/// The periodic chores of remote work (ADR-0005): runs that waited too long expire (and the streams hear it), a run whose
/// target went quiet fails, grants expire, offline alerts open and close, and old metrics, alerts and runs are deleted in
/// batches. Every status change is a compare-and-set, so it never overrides an answer that arrived first.
/// </summary>
public static class RemoteHousekeeping
{
    public const int Batch = 1000;

    // An array, not the contract's set: EF Core translates Contains on an array (IN), not on an IReadOnlySet.
    private static readonly string[] Final = [.. RunStatuses.Final];
    public const string Lost = "target_lost";

    /// <summary>A running run is lost once this long past its own timeout without a final report.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(5);

    public static async Task RunOnceAsync(MonitorDb db, ApiConfig config, Broker broker, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        await ExpireRunsAsync(db, broker, now, ct);
        await db.MachineGrants.Where(g => (g.Status == GrantStatuses.Active || g.Status == GrantStatuses.Requested) && g.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Expired), ct);
        await OfflineAsync(db, config, broker, now, ct);
        await RetentionAsync(db, config, now, ct);
    }

    private static async Task ExpireRunsAsync(MonitorDb db, Broker broker, DateTimeOffset now, CancellationToken ct)
    {
        var expiring = await db.RemoteRuns.AsNoTracking()
            .Where(r => (r.Status == RunStatuses.PendingApproval || r.Status == RunStatuses.Approved || r.Status == RunStatuses.Delivered)
                        && r.ExpiresAt <= now)
            .Take(Batch).ToListAsync(ct);
        foreach (var run in expiring)
        {
            var changed = await db.RemoteRuns.Where(r => r.Id == run.Id && r.Status == run.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Expired).SetProperty(r => r.EndedAt, now), ct);
            if (changed != 1) continue;
            var was = run.Status;
            run.Status = RunStatuses.Expired;
            RunNotices.Changed(broker, run);
            if (was != RunStatuses.PendingApproval) RunNotices.Cancel(broker, run);
        }

        var lost = await db.RemoteRuns.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running && r.StartedAt != null)
            .Where(r => r.StartedAt!.Value.AddSeconds(r.TimeoutSeconds) + LostAfter <= now)
            .Take(Batch).ToListAsync(ct);
        foreach (var run in lost)
        {
            var changed = await db.RemoteRuns.Where(r => r.Id == run.Id && r.Status == RunStatuses.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Failed).SetProperty(r => r.EndedAt, now)
                    .SetProperty(r => r.Error, Lost), ct);
            if (changed != 1) continue;
            run.Status = RunStatuses.Failed;
            RunNotices.Changed(broker, run);
        }
    }

    /// <summary>A service agent that stopped reporting gets one open "offline" alert; it resolves when the agent is back.</summary>
    private static async Task OfflineAsync(MonitorDb db, ApiConfig config, Broker broker, DateTimeOffset now, CancellationToken ct)
    {
        var silentSince = now - config.OfflineAfter;
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.Status == AgentStatuses.Active && a.ServiceMode)
            .Select(a => new { a.Id, a.WorkspaceId, a.LastHeartbeatAt }).ToListAsync(ct);
        var ids = agents.Select(a => a.Id).ToList();
        var open = await db.MachineAlerts.AsNoTracking()
            .Where(x => ids.Contains(x.AgentId) && x.Kind == AlertKinds.Offline && x.State == AlertStates.Open)
            .Select(x => x.AgentId).ToListAsync(ct);
        foreach (var a in agents)
        {
            var silent = a.LastHeartbeatAt is null || a.LastHeartbeatAt <= silentSince;
            var minutes = a.LastHeartbeatAt is { } at ? (float)Math.Min((now - at).TotalMinutes, 100_000) : 0;
            if (silent == open.Contains(a.Id)) continue;
            if (silent) await MachineReports.OpenAsync(db, a.Id, a.WorkspaceId, AlertKinds.Offline, "", minutes, (float)config.OfflineAfter.TotalMinutes, now, ct);
            else await MachineReports.ResolveAsync(db, a.Id, AlertKinds.Offline, "", minutes, now, ct);
            broker.Publish(Broker.Workspace(a.WorkspaceId), new StreamMessage(MachineReports.AlertEvent,
                new { agentId = a.Id, kind = AlertKinds.Offline, state = silent ? AlertStates.Open : AlertStates.Resolved }));
        }
    }

    private static async Task RetentionAsync(MonitorDb db, ApiConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var metricsBefore = now - config.MetricsRetention;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM machine_metrics WHERE ctid IN (SELECT ctid FROM machine_metrics WHERE sampled_at < {metricsBefore} LIMIT {Batch})", ct);
        var old = await (from r in db.RemoteRuns.AsNoTracking()
                         join s in db.WorkspaceSettings.AsNoTracking() on r.WorkspaceId equals s.WorkspaceId
                         where Final.Contains(r.Status) && r.CreatedAt < now.AddDays(-s.RetentionDays)
                         select r.Id).Take(Batch).ToListAsync(ct);
        if (old.Count > 0)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The runs are locked before their output is deleted: account deletion takes a run's row, then its output, and
            // output first here would meet it from the other side (40P01); the global order is runs, then output.
            await RemoteLocks.RunsAsync(db, r => old.Contains(r.Id), ct);
            await db.RemoteRunOutput.Where(o => old.Contains(o.RunId)).ExecuteDeleteAsync(ct);
            await db.RemoteRuns.Where(r => old.Contains(r.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        var alerts = await (from x in db.MachineAlerts.AsNoTracking()
                            join s in db.WorkspaceSettings.AsNoTracking() on x.WorkspaceId equals s.WorkspaceId
                            where x.State == AlertStates.Resolved && x.ResolvedAt < now.AddDays(-s.RetentionDays)
                            select x.Id).Take(Batch).ToListAsync(ct);
        if (alerts.Count > 0) await db.MachineAlerts.Where(x => alerts.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }
}
