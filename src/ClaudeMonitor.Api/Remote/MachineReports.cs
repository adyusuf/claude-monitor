using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// What an agent reports about its machine (ADR-0005): its profile (exec level, service mode), metric samples and the
/// alerts it opened or resolved. Samples are idempotent per (agent, time) and a time outside the allowed clock skew is
/// dropped. Text from the machine (mounts, OS version) is cut and stripped of control characters.
/// </summary>
public static class MachineReports
{
    public const string AlertEvent = "alert";
    public const int SubjectMax = 200;
    public const int OsVersionMax = 100;

    public static async Task ProfileAsync(MonitorDb db, Guid agentId, AgentProfile profile, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var agent = await db.Agents.FirstAsync(a => a.Id == agentId, ct);
        agent.ExecLevel = ExecLevels.All.Contains(profile.ExecLevel) ? profile.ExecLevel : ExecLevels.Off;
        agent.ServiceMode = profile.ServiceMode;
        agent.ProfileAt = now;
        if (profile.OsVersion is { Length: > 0 } version)
        {
            var clean = Clean(version, OsVersionMax);
            await db.Machines.Where(m => m.Id == agent.MachineId).ExecuteUpdateAsync(s => s.SetProperty(m => m.OsVersion, clean), ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stores the samples; answers how many were stored (a duplicate or one outside the skew is not).</summary>
    public static async Task<int> MetricsAsync(MonitorDb db, ApiConfig config, Guid agentId, Guid workspaceId, MetricsReport report,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        var stored = 0;
        foreach (var s in (report?.Samples ?? []).Take(config.MetricsReportMax))
        {
            if (s is null || s.SampledAt > now + config.ClockSkewMax || s.SampledAt < now - config.ClockSkewMax - config.MetricsReportWindow
                || s.MemTotalBytes < 0 || s.MemUsedBytes < 0 || double.IsNaN(s.CpuPct))
            {
                continue;
            }

            var disks = JsonSerializer.Serialize((s.Disks ?? []).Take(config.MetricsDisksMax)
                .Where(d => d is not null && d.TotalBytes > 0 && d.UsedBytes >= 0)
                .Select(d => new { mount = Clean(d.Mount ?? "", SubjectMax), usedBytes = d.UsedBytes, totalBytes = d.TotalBytes }));
            var cpu = (float)Math.Clamp(s.CpuPct, 0, 100);
            stored += await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO machine_metrics (agent_id, sampled_at, workspace_id, received_at, cpu_pct, mem_used_bytes, mem_total_bytes, disks)
                VALUES ({agentId}, {s.SampledAt}, {workspaceId}, {now}, {cpu}, {s.MemUsedBytes}, {s.MemTotalBytes}, {disks}::jsonb)
                ON CONFLICT (agent_id, sampled_at) DO NOTHING
                """, ct);
        }

        return stored;
    }

    /// <summary>Opens (one open per agent, kind and subject) or resolves alerts; an agent never reports "offline".</summary>
    public static async Task AlertsAsync(MonitorDb db, Broker broker, Guid agentId, Guid workspaceId, IReadOnlyList<AlertReport> reports,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(broker);
        foreach (var r in (reports ?? []).Take(32))
        {
            if (r is null || !AlertKinds.FromAgent.Contains(r.Kind) || double.IsNaN(r.Value)) continue;
            var subject = Clean(r.Subject ?? "", SubjectMax);
            var value = (float)Math.Clamp(r.Value, 0, 100);
            var threshold = (float)Math.Clamp(r.ThresholdPct, 0, 100);
            switch (r.State)
            {
                case AlertStates.Open:
                    await OpenAsync(db, agentId, workspaceId, r.Kind, subject, value, threshold, now, ct);
                    break;
                case AlertStates.Resolved:
                    await ResolveAsync(db, agentId, r.Kind, subject, value, now, ct);
                    break;
                default:
                    continue;
            }

            broker.Publish(Broker.Workspace(workspaceId), new StreamMessage(AlertEvent, new { agentId, kind = r.Kind, state = r.State }));
        }
    }

    public static Task<int> OpenAsync(MonitorDb db, Guid agentId, Guid workspaceId, string kind, string subject, float value,
        float threshold, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO machine_alerts (id, workspace_id, agent_id, kind, subject, state, threshold_pct, last_value, peak_value, opened_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {workspaceId}, {agentId}, {kind}, {subject}, 'open', {threshold}, {value}, {value}, {now}, {now})
            ON CONFLICT (agent_id, kind, subject) WHERE state = 'open'
            DO UPDATE SET last_value = EXCLUDED.last_value, peak_value = GREATEST(machine_alerts.peak_value, EXCLUDED.last_value),
                          threshold_pct = EXCLUDED.threshold_pct, updated_at = EXCLUDED.updated_at
            """, ct);

    public static Task<int> ResolveAsync(MonitorDb db, Guid agentId, string kind, string subject, float value, DateTimeOffset now,
        CancellationToken ct) =>
        db.MachineAlerts.Where(a => a.AgentId == agentId && a.Kind == kind && a.Subject == subject && a.State == AlertStates.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AlertStates.Resolved).SetProperty(a => a.ResolvedAt, now)
                .SetProperty(a => a.LastValue, value).SetProperty(a => a.UpdatedAt, now), ct);

    public static string Clean(string text, int max)
    {
        var cleaned = new string((text ?? "").Where(ch => !char.IsControl(ch) && ch is not ('‮' or '‭' or '​')).ToArray());
        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }
}
