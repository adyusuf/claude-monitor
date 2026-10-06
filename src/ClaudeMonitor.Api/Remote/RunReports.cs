using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// What a target reports about a run it was sent: its status and its output. Only the run's target may report; a status
/// moves only forward (compare-and-set), and output is idempotent per (run, seq) and capped atomically per run.
/// </summary>
public sealed class RunReports(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    private const int ErrorChars = 500;

    public async Task<IResult> StatusAsync(Guid id, RunStatusUpdate req, Guid agentId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        if (!RunStatuses.FromAgent.Contains(req.Status ?? "")) return Http.Invalid("status", "invalid_status");
        var run = await db.RemoteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TargetAgentId == agentId, ct);
        if (run is null) return Http.NotFound();
        if (RunStatuses.Final.Contains(run.Status)) return Results.Conflict();

        var now = clock.GetUtcNow();
        var at = req.At is { } t && t <= now && t >= now - config.ClockSkewMax ? t : now;
        var error = req.Error is { } e ? Clean(e) : null;
        var exe = req.ResolvedExe is { } x ? Clean(x) : null;
        int changed;
        switch (req.Status)
        {
            case RunStatuses.Delivered:
                changed = await db.RemoteRuns.Where(r => r.Id == id && r.Status == RunStatuses.Approved)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Delivered).SetProperty(r => r.DeliveredAt, now), ct);
                break;
            case RunStatuses.Running:
                changed = await db.RemoteRuns.Where(r => r.Id == id && (r.Status == RunStatuses.Approved || r.Status == RunStatuses.Delivered))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Running).SetProperty(r => r.StartedAt, at)
                        .SetProperty(r => r.ResolvedExe, exe), ct);
                break;
            default:
                changed = await db.RemoteRuns.Where(r => r.Id == id && RunStatuses.Live.Contains(r.Status))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, req.Status!).SetProperty(r => r.EndedAt, at)
                        .SetProperty(r => r.ExitCode, req.ExitCode).SetProperty(r => r.Error, error)
                        .SetProperty(r => r.OutputTruncated, r => r.OutputTruncated || req.OutputTruncated), ct);
                if (changed == 1)
                {
                    Audit.Add(db, http, clock, AuditActions.RunFinished, run.WorkspaceId, agentId: agentId, targetType: "run",
                        targetId: id, detail: new { status = req.Status, exitCode = req.ExitCode });
                    await db.SaveChangesAsync(ct);
                }

                break;
        }

        if (changed != 1) return Results.Conflict();
        run.Status = req.Status!;
        RunNotices.Changed(broker, run);
        return Results.NoContent();
    }

    /// <summary>
    /// Stores chunks the target sent. A chunk already stored is skipped; a chunk that would take the run past its cap is
    /// not stored and the run is marked truncated. Chunks are accepted only while the run is live.
    /// </summary>
    public async Task<IResult> OutputAsync(Guid id, IReadOnlyList<RunOutputChunk> chunks, Guid agentId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var ct = http.RequestAborted;
        if (chunks.Count is 0 or > 16) return Http.Invalid("chunks", "invalid_count");
        foreach (var c in chunks)
        {
            if (c.Seq < 0 || !RunStreams.All.Contains(c.Stream) || c.Body is null || c.Body.Contains('\0')) return Http.Invalid("chunks", "invalid_chunk");
            var bytes = System.Text.Encoding.UTF8.GetByteCount(c.Body);
            if (bytes is < 1 || bytes > config.RunChunkMax) return Http.Invalid("chunks", "invalid_chunk");
        }

        var run = await db.RemoteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TargetAgentId == agentId, ct);
        if (run is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        foreach (var c in chunks)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(c.Body);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO remote_run_output (run_id, seq, stream, body, bytes, gap_before, received_at)
                VALUES ({id}, {c.Seq}, {c.Stream}, {c.Body}, {bytes}, {c.GapBefore}, {now})
                ON CONFLICT (run_id, seq) DO NOTHING
                """, ct);
            if (inserted == 0)
            {
                await tx.CommitAsync(ct);
                continue;
            }

            var counted = await db.RemoteRuns
                .Where(r => r.Id == id && RunStatuses.Live.Contains(r.Status) && r.OutputBytes + bytes <= config.RunOutputMax)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.OutputBytes, r => r.OutputBytes + bytes), ct);
            if (counted == 1)
            {
                await tx.CommitAsync(ct);
                continue;
            }

            await tx.RollbackAsync(ct);
            await db.RemoteRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.OutputTruncated, true), ct);
        }

        return Results.NoContent();
    }

    private static string Clean(string text)
    {
        var cleaned = new string(text.Where(ch => !char.IsControl(ch)).ToArray());
        return cleaned.Length > ErrorChars ? cleaned[..ErrorChars] : cleaned;
    }
}
