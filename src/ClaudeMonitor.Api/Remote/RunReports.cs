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

    // An array, not the contract's set: EF Core translates Contains on an array (IN), not on an IReadOnlySet.
    private static readonly string[] Live = [.. RunStatuses.Live];

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
                changed = await db.RemoteRuns.Where(r => r.Id == id && Live.Contains(r.Status))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, req.Status!).SetProperty(r => r.EndedAt, at)
                        .SetProperty(r => r.ExitCode, req.ExitCode).SetProperty(r => r.Error, error)
                        .SetProperty(r => r.OutputTruncated, r => r.OutputTruncated || req.OutputTruncated), ct);
                if (changed == 1)
                {
                    Audit.Add(db, http, clock, AuditActions.RunFinished, run.WorkspaceId, agentId: agentId, targetType: "run",
                        targetId: id, detail: new { status = req.Status, exitCode = req.ExitCode });
                    await db.SaveChangesAsync(ct);
                    if (run.Mode == RunModes.Script) await StoreOutputAccountAsync(id, req, ct);
                    if (run.Kind == RunKinds.Test) await StoreTestCountsAsync(id, ct);
                }

                break;
        }

        if (changed != 1) return Results.Conflict();
        run.Status = req.Status!;
        RunNotices.Changed(broker, run);
        return Results.NoContent();
    }

    /// <summary>
    /// What the target says it read, dropped and sent (ADR-0008 §3d), and the verdict: complete only when nothing was dropped on the target,
    /// nothing was refused here and the target did not stop reading at its limit.
    /// </summary>
    private async Task StoreOutputAccountAsync(Guid id, RunStatusUpdate req, CancellationToken ct)
    {
        var dropped = req.OutputDroppedBytes ?? 0;
        var truncated = req.OutputTruncated;
        await db.RemoteRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.OutputReadBytes, req.OutputReadBytes)
            .SetProperty(r => r.OutputDroppedBytes, req.OutputDroppedBytes).SetProperty(r => r.OutputChunks, req.OutputChunks)
            .SetProperty(r => r.OutputComplete, r => (r.OutputRefusedBytes ?? 0) == 0 && dropped == 0 && !truncated), ct);
    }

    /// <summary>A script run refuses every chunk after the first one over its cap, so what is missing is one range from <paramref name="seq"/> on.</summary>
    private async Task RefuseAsync(Guid id, int seq, int bytes, CancellationToken ct) =>
        await db.RemoteRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.OutputTruncated, true)
            .SetProperty(r => r.OutputMissingFromSeq, r => r.OutputMissingFromSeq == null || r.OutputMissingFromSeq > seq ? seq : r.OutputMissingFromSeq)
            .SetProperty(r => r.OutputRefusedBytes, r => (r.OutputRefusedBytes ?? 0) + bytes), ct);

    /// <summary>The counts of a finished <c>test</c> run, read from the tail of its output; left empty when the tool is not recognised.</summary>
    private async Task StoreTestCountsAsync(Guid id, CancellationToken ct)
    {
        var tail = await db.RemoteRunOutput.AsNoTracking().Where(o => o.RunId == id).OrderByDescending(o => o.Seq).Take(24)
            .Select(o => new { o.Seq, o.Body }).ToListAsync(ct);
        if (TestSummary.Parse(string.Concat(tail.OrderBy(o => o.Seq).Select(o => o.Body))) is not { } counts) return;
        await db.RemoteRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.TestsPassed, counts.Passed)
            .SetProperty(r => r.TestsFailed, counts.Failed).SetProperty(r => r.TestsSkipped, counts.Skipped), ct);
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
        var script = run.Mode == RunModes.Script;
        var limit = script ? run.OutputCap ?? config.RunOutputMax : config.RunOutputMax;
        var refusing = script && run.OutputMissingFromSeq is not null;
        foreach (var c in chunks)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(c.Body);
            if (refusing)
            {
                await RefuseAsync(id, c.Seq, bytes, ct);
                continue;
            }

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
                .Where(r => r.Id == id && Live.Contains(r.Status) && r.OutputBytes + bytes <= limit)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.OutputBytes, r => r.OutputBytes + bytes), ct);
            if (counted == 1)
            {
                await tx.CommitAsync(ct);
                continue;
            }

            await tx.RollbackAsync(ct);
            if (script)
            {
                refusing = true;
                await RefuseAsync(id, c.Seq, bytes, ct);
                continue;
            }

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
