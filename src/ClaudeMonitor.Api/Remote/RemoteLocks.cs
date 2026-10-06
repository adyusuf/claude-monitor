using System.Linq.Expressions;
using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// Row locks for transactions that touch sets of remote-work rows (a member removed, an agent revoked or moved, an account
/// deleted). PostgreSQL gives a multi-row UPDATE no row order, so two of them over overlapping rows can wait on each other
/// (40P01). The one global order, shared with <see cref="RunCreator"/>: settings, members (by workspace, then user id),
/// agents (by id), jobs, grants, alerts, runs; within a table by id; one row per statement, using the no-op
/// <c>ExecuteUpdate</c> idiom inside the ambient transaction. Call the methods in that order, before the bulk update.
/// </summary>
public static class RemoteLocks
{
    public static async Task JobsAsync(MonitorDb db, Expression<Func<MachineJob, bool>> filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = await db.MachineJobs.AsNoTracking().Where(filter).OrderBy(j => j.Id).Select(j => j.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await db.MachineJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, j => j.Status), ct);
        }
    }

    public static async Task GrantsAsync(MonitorDb db, Expression<Func<MachineGrant, bool>> filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = await db.MachineGrants.AsNoTracking().Where(filter).OrderBy(g => g.Id).Select(g => g.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await db.MachineGrants.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, g => g.Status), ct);
        }
    }

    public static async Task AlertsAsync(MonitorDb db, Expression<Func<MachineAlert, bool>> filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = await db.MachineAlerts.AsNoTracking().Where(filter).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await db.MachineAlerts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, a => a.State), ct);
        }
    }

    public static async Task RunsAsync(MonitorDb db, Expression<Func<RemoteRun, bool>> filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = await db.RemoteRuns.AsNoTracking().Where(filter).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await db.RemoteRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, r => r.Status), ct);
        }
    }

    /// <summary>Active agents of these ids, by id.</summary>
    public static async Task AgentsAsync(MonitorDb db, IEnumerable<Guid> agentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agentIds);
        foreach (var id in agentIds.Distinct().Order())
        {
            await db.Agents.Where(a => a.Id == id && a.Status == AgentStatuses.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecLevel, a => a.ExecLevel), ct);
        }
    }
}
