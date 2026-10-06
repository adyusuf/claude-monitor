using System.IO.Pipelines;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// The remote-work part of a user's own data (ADR-0004; docs/data-model.md §3b). Export: runs they asked for or own as
/// target, with their output, their grants and jobs, and the alerts and metrics of their machines. Deletion: open
/// runs are cancelled, then commands, templates, working directories and output of those runs and the user's grants
/// and jobs are emptied (the rows stay, other people's records point at them), and their machines' metrics and alerts go.
/// </summary>
public static class PrivacyRemoteData
{
    private const int ChunkSize = 500;

    public static async Task ExportAsync(Utf8JsonWriter w, PipeWriter body, MonitorDb db, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(db);
        var runs = await db.RemoteRuns.AsNoTracking().Where(r => r.RequesterUserId == userId || r.TargetUserId == userId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        w.WriteStartArray("remoteRuns");
        foreach (var r in runs)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("mode", r.Mode);
            PrivacyEndpoints.Write(w, "argv", r.Argv);
            w.WriteString("shellCommand", r.ShellCommand);
            w.WriteString("cwd", r.Cwd);
            w.WriteString("status", r.Status);
            w.WriteString("createdAt", r.CreatedAt);
            WriteTime(w, "startedAt", r.StartedAt);
            WriteTime(w, "endedAt", r.EndedAt);
            if (r.ExitCode is { } exit) w.WriteNumber("exitCode", exit);
            else w.WriteNull("exitCode");
            w.WriteStartArray("output");
            var after = -1;
            while (true)
            {
                var chunk = await db.RemoteRunOutput.AsNoTracking().Where(o => o.RunId == r.Id && o.Seq > after)
                    .OrderBy(o => o.Seq).Take(ChunkSize).ToListAsync(ct);
                if (chunk.Count == 0) break;
                foreach (var o in chunk) JsonSerializer.Serialize(w, new { o.Seq, o.Stream, o.Body, o.GapBefore }, PrivacyEndpoints.Json);
                after = chunk[^1].Seq;
                await Flush(w, body, ct);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        PrivacyEndpoints.Write(w, "grants", await db.MachineGrants.AsNoTracking().Where(g => g.OwnerUserId == userId || g.GranteeUserId == userId)
            .OrderBy(g => g.CreatedAt).Select(g => new
            {
                g.Id,
                g.TargetAgentId,
                g.OwnerUserId,
                g.GranteeUserId,
                g.Template,
                g.Cwd,
                g.MaxTimeoutSeconds,
                g.Reason,
                g.Status,
                g.CreatedAt,
                g.ExpiresAt,
                g.DecidedAt,
                g.RevokedAt,
                g.UseCount,
                g.LastUsedAt,
            }).ToListAsync(ct));
        PrivacyEndpoints.Write(w, "jobs", await db.MachineJobs.AsNoTracking().Where(j => j.OwnerUserId == userId || j.ProposedByUserId == userId)
            .OrderBy(j => j.CreatedAt).Select(j => new
            {
                j.Id,
                j.TargetAgentId,
                j.OwnerUserId,
                j.ProposedByUserId,
                j.Name,
                j.Argv,
                j.Cwd,
                j.TimeoutSeconds,
                j.Reason,
                j.Status,
                j.CreatedAt,
                j.DecidedAt,
                j.RetiredAt,
            }).ToListAsync(ct));

        var agentIds = db.Agents.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.Id);
        PrivacyEndpoints.Write(w, "alerts", await db.MachineAlerts.AsNoTracking().Where(a => agentIds.Contains(a.AgentId)).OrderBy(a => a.OpenedAt)
            .Select(a => new { a.Id, a.AgentId, a.Kind, a.Subject, a.State, a.ThresholdPct, a.LastValue, a.PeakValue, a.OpenedAt, a.ResolvedAt })
            .ToListAsync(ct));
        w.WriteStartArray("metrics");
        foreach (var agentId in await agentIds.ToListAsync(ct))
        {
            var since = DateTimeOffset.MinValue;
            while (true)
            {
                var chunk = await db.MachineMetrics.AsNoTracking().Where(m => m.AgentId == agentId && m.SampledAt > since)
                    .OrderBy(m => m.SampledAt).Take(ChunkSize).ToListAsync(ct);
                if (chunk.Count == 0) break;
                foreach (var m in chunk)
                {
                    using var disks = JsonDocument.Parse(m.Disks);
                    JsonSerializer.Serialize(w, new { m.AgentId, m.SampledAt, m.CpuPct, m.MemUsedBytes, m.MemTotalBytes, Disks = disks.RootElement },
                        PrivacyEndpoints.Json);
                }

                since = chunk[^1].SampledAt;
                await Flush(w, body, ct);
            }
        }

        w.WriteEndArray();
    }

    /// <summary>Inside the deletion's transaction, before the user row changes: cancel, then empty.</summary>
    public static async Task DeleteAsync(MonitorDb db, Broker broker, Guid userId, List<Guid> agentIds, DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agentIds);
        await RunDecisions.CancelOpenAsync(db, broker, now, null, null, userId, ct);
        var runIds = db.RemoteRuns.Where(r => r.RequesterUserId == userId || r.TargetUserId == userId).Select(r => r.Id);
        await db.RemoteRunOutput.Where(o => runIds.Contains(o.RunId)).ExecuteDeleteAsync(ct);
        await db.RemoteRuns.Where(r => r.RequesterUserId == userId || r.TargetUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Argv, (List<string>?)null).SetProperty(r => r.ShellCommand, (string?)null)
                .SetProperty(r => r.Cwd, (string?)null).SetProperty(r => r.Reason, (string?)null)
                .SetProperty(r => r.ResolvedExe, (string?)null).SetProperty(r => r.Error, (string?)null), ct);
        await db.MachineGrants.Where(g => (g.OwnerUserId == userId || g.GranteeUserId == userId)
                                          && (g.Status == GrantStatuses.Active || g.Status == GrantStatuses.Requested))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Revoked).SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokedBy, userId), ct);
        await db.MachineGrants.Where(g => g.OwnerUserId == userId || g.GranteeUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Template, (List<string>?)null).SetProperty(g => g.Cwd, (string?)null)
                .SetProperty(g => g.Reason, (string?)null), ct);
        await db.MachineJobs.Where(j => (j.OwnerUserId == userId || j.ProposedByUserId == userId)
                                        && (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Retired).SetProperty(j => j.RetiredAt, now), ct);
        await db.MachineJobs.Where(j => j.OwnerUserId == userId || j.ProposedByUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Argv, (List<string>?)null).SetProperty(j => j.Cwd, (string?)null)
                .SetProperty(j => j.Reason, (string?)null), ct);
        await db.MachineMetrics.Where(m => agentIds.Contains(m.AgentId)).ExecuteDeleteAsync(ct);
        await db.MachineAlerts.Where(a => agentIds.Contains(a.AgentId)).ExecuteDeleteAsync(ct);
    }

    private static void WriteTime(Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is { } at) w.WriteString(name, at);
        else w.WriteNull(name);
    }

    private static async Task Flush(Utf8JsonWriter w, PipeWriter body, CancellationToken ct)
    {
        await w.FlushAsync(ct);
        await body.FlushAsync(ct);
    }
}
