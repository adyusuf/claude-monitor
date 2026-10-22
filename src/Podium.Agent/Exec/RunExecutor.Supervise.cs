using System.ComponentModel;
using System.Threading.Channels;
using Podium.Contracts;

namespace Podium.Agent.Exec;

/// <summary>The started run: output streaming, the timeout, the kill, and exactly one final status.</summary>
public sealed partial class RunExecutor
{
    private async Task<RunResult> SuperviseAsync(RunMessage run, IRunProcess process, string? exe, TimeSpan timeout, long began,
        Func<RunOutputChunk, Task> onChunk, Func<RunStatusUpdate, Task> onStatus, CancellationToken cancel)
    {
        var collector = CollectorFor(run);
        using var stop = new CancellationTokenSource();
        var readers = Task.WhenAll(collector.PumpAsync(RunStreams.Stdout, process.Stdout, stop.Token),
            collector.PumpAsync(RunStreams.Stderr, process.Stderr, stop.Token));
        var sender = SendAsync(collector.Chunks, onChunk);
        using var flush = clock.CreateTimer(_ => collector.FlushHead(), null, config.FlushEvery, config.FlushEvery);

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancel.Register(() => cancelled.TrySetResult());
        using var timer = new CancellationTokenSource();
        var timedOut = Task.Delay(timeout, clock, timer.Token);

        var first = await Task.WhenAny(process.Exited, timedOut, cancelled.Task, collector.CapReached);
        var kill = first == process.Exited ? null
            : first == timedOut ? KillReasons.Timeout
            : first == cancelled.Task ? KillReasons.Cancelled
            : KillReasons.OutputLimit;
        await timer.CancelAsync();
        if (kill is not null) await TerminateAsync(run, process);

        var exit = await ExitOf(run, process, ExitWait);
        if (!await ReadersDoneAsync(readers))
        {
            kill ??= KillReasons.Straggler; // the lead is gone but something it started still holds the pipes
            TryKill(run, process.KillNow);
            await ReadersDoneAsync(readers);
        }

        await stop.CancelAsync();
        await flush.DisposeAsync();
        collector.Complete();
        await sender;

        var (status, code, error) = kill switch
        {
            KillReasons.Timeout => (RunStatuses.TimedOut, (int?)null, (string?)null),
            KillReasons.Cancelled => (RunStatuses.Cancelled, null, null),
            KillReasons.OutputLimit => (RunStatuses.Failed, null, RunFailures.OutputLimit),
            _ when exit is null => (RunStatuses.Failed, null, RunFailures.ExitUnknown),
            _ when exit.Code == 0 && exit.Signal is null => (RunStatuses.Succeeded, 0, null),
            _ => (RunStatuses.Failed, exit.Code, exit.Signal is { } s ? $"{RunFailures.SignalPrefix}{s}" : null),
        };
        return await FinishAsync(run, status, code, error, exe, began, onStatus, kill ?? None, collector);
    }

    /// <summary>
    /// An argv or shell run keeps the 256 KiB head and tail of ADR-0005. A script run keeps what its workspace allows (ADR-0008 §3d): the cap frozen
    /// at creation, at most the agent's own ceiling, as a head of the cap less the 256 KiB tail and that tail; the read limit that kills the run stays.
    /// </summary>
    private OutputCollector CollectorFor(RunMessage run)
    {
        if (run.Mode != RunModes.Script) return new OutputCollector(config);
        var cap = Math.Clamp(run.OutputCap ?? ScriptDefaultCap, ScriptMinCap, Math.Max(ScriptMinCap, config.ExecScriptOutputCeiling));
        var tail = Math.Min(ScriptTailBytes, cap / 2);
        return new OutputCollector((int)Math.Min(int.MaxValue, cap - tail), (int)tail, config.RunChunkBytes, config.RunReadCap);
    }

    // The polite stop, the grace period, then the hard one whether or not the lead left (its children may ignore the first).
    private async Task TerminateAsync(RunMessage run, IRunProcess process)
    {
        TryKill(run, process.Kill);
        await Task.WhenAny(process.Exited, Task.Delay(config.ExecKillGrace, clock));
        TryKill(run, process.KillNow);
    }

    private void TryKill(RunMessage run, Action kill)
    {
        try
        {
            kill();
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
        {
            log.Write($"run {run.Id}: kill failed ({e.GetType().Name})");
        }
    }

    private async Task<RunExit?> ExitOf(RunMessage run, IRunProcess process, TimeSpan wait)
    {
        if (await Task.WhenAny(process.Exited, Task.Delay(wait, clock)) != process.Exited) return null;
        try
        {
            return await process.Exited;
        }
        catch (Exception e) when (e is IOException or Win32Exception)
        {
            log.Write($"run {run.Id}: exit unknown ({e.GetType().Name})");
            return null;
        }
    }

    private async Task<bool> ReadersDoneAsync(Task readers) =>
        await Task.WhenAny(readers, Task.Delay(DrainWait, clock)) == readers && readers.IsCompletedSuccessfully;

    private static async Task SendAsync(ChannelReader<RunOutputChunk> chunks, Func<RunOutputChunk, Task> onChunk)
    {
        await foreach (var chunk in chunks.ReadAllAsync())
        {
            await onChunk(chunk);
        }
    }

    private async Task<RunResult> RefuseAsync(RunMessage run, string error, string? exe, long began, Func<RunStatusUpdate, Task> onStatus)
    {
        log.Write($"run refused id={run.Id} grant={GrantOf(run)} reason={error}");
        return await FinishAsync(run, RunStatuses.Failed, null, error, exe, began, onStatus, None, null, logEnd: false);
    }

    private async Task<RunResult> FinishAsync(RunMessage run, string status, int? code, string? error, string? exe, long began,
        Func<RunStatusUpdate, Task> onStatus, string kill, OutputCollector? collector, bool logEnd = true)
    {
        var result = new RunResult(status, code, error, collector?.Truncated ?? false, exe, collector?.BytesRead ?? 0, clock.GetElapsedTime(began),
            collector?.BytesDropped ?? 0, collector?.ChunksProduced ?? 0);
        if (logEnd)
        {
            log.Write($"run end id={run.Id} grant={GrantOf(run)} status={status} exit={code?.ToString() ?? None} read_bytes={result.BytesRead} "
                + $"kept_bytes={collector?.BytesKept ?? 0} truncated={result.OutputTruncated} duration_ms={(long)result.Duration.TotalMilliseconds} kill={kill}");
        }

        await onStatus(result.ToUpdate(clock.GetUtcNow(), run.Mode == RunModes.Script));
        return result;
    }
}
