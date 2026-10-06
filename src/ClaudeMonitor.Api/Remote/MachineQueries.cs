using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>Reads of a workspace's machines, their metrics and alerts, for agents (MCP tools) and the web. Callers check membership.</summary>
public static class MachineQueries
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Active agents of the workspace, service agents first, then by host name.</summary>
    public static async Task<List<MachineView>> MachinesAsync(MonitorDb db, ApiConfig config, Guid workspaceId, DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        var online = now - config.OnlineWindow;
        var rows = await (from a in db.Agents.AsNoTracking()
                          join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                          join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                          where a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active
                          orderby a.ServiceMode descending, m.Hostname, a.Id
                          select new
                          {
                              a.Id, m.Hostname, m.Os, u.DisplayName, a.ExecLevel, a.ServiceMode, a.LastHeartbeatAt,
                              Alerts = db.MachineAlerts.Count(x => x.AgentId == a.Id && x.State == AlertStates.Open),
                          }).Take(500).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var since = now - config.MetricsReportWindow;
        var latest = await db.MachineMetrics.AsNoTracking()
            .Where(x => ids.Contains(x.AgentId) && x.SampledAt > since)
            .GroupBy(x => x.AgentId)
            .Select(g => g.OrderByDescending(x => x.SampledAt).First())
            .ToListAsync(ct);
        return rows.Select(r => new MachineView(r.Id, r.Hostname, r.Os, r.DisplayName, r.ExecLevel, r.ServiceMode,
            r.LastHeartbeatAt > online, r.LastHeartbeatAt, latest.FirstOrDefault(x => x.AgentId == r.Id) is { } s ? Sample(s) : null,
            r.Alerts)).ToList();
    }

    public static async Task<List<MetricSample>> MetricsAsync(MonitorDb db, ApiConfig config, Guid workspaceId, Guid agentId,
        int minutes, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        var since = now.AddMinutes(-Math.Clamp(minutes, 1, config.MetricsPointsMax));
        var rows = await db.MachineMetrics.AsNoTracking()
            .Where(x => x.AgentId == agentId && x.WorkspaceId == workspaceId && x.SampledAt > since)
            .OrderBy(x => x.SampledAt).Take(config.MetricsPointsMax).ToListAsync(ct);
        return rows.Select(Sample).ToList();
    }

    public static async Task<List<AlertView>> AlertsAsync(MonitorDb db, Guid workspaceId, Guid? agentId, bool includeResolved,
        int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await (from x in db.MachineAlerts.AsNoTracking()
                      join a in db.Agents.AsNoTracking() on x.AgentId equals a.Id
                      join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                      where x.WorkspaceId == workspaceId && (agentId == null || x.AgentId == agentId)
                            && (includeResolved || x.State == AlertStates.Open)
                      orderby x.OpenedAt descending, x.Id descending
                      select new AlertView(x.Id, x.AgentId, m.Hostname, x.Kind, x.Subject, x.State, x.ThresholdPct, x.LastValue,
                          x.PeakValue, x.OpenedAt, x.ResolvedAt)).Take(limit).ToListAsync(ct);
    }

    private static MetricSample Sample(MachineMetric m) => new(m.SampledAt, m.CpuPct, m.MemUsedBytes, m.MemTotalBytes,
        JsonSerializer.Deserialize<List<DiskSample>>(m.Disks, Json) ?? []);
}
