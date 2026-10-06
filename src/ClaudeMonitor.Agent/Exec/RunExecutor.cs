using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>Why the executor itself failed or refused a run; sent back as the run's error (the guard has its own, <see cref="ExecErrors"/>).</summary>
public static class RunFailures
{
    public const string RunningAsRoot = "running_as_root";
    public const string TooLate = "too_late";
    public const string TargetBusy = RemoteErrors.TargetBusy;
    public const string OutputLimit = "output_limit";
    public const string SpawnFailed = "spawn_failed";
    public const string BadRun = "bad_run";
    public const string GuardFailed = "guard_failed";
    public const string ExitUnknown = "exit_unknown";
    public const string SignalPrefix = "signal_";
}

/// <summary>The one final answer on a run. Duration is on the monotonic clock; BytesRead is raw output including what was dropped.</summary>
public sealed record RunResult(string Status, int? ExitCode, string? Error, bool OutputTruncated, string? ResolvedExe, long BytesRead,
    TimeSpan Duration)
{
    public RunStatusUpdate ToUpdate(DateTimeOffset at) => new(Status, ExitCode, Error, OutputTruncated, ResolvedExe, at);
}

/// <summary>
/// Runs approved runs on this target (ADR-0005, "The executor on the target"): refuses as root, too late, busy or not allowed,
/// starts the run in a tree that can be killed whole, streams its masked output, enforces the timeout on a monotonic timer
/// and reports exactly one final status. It logs the run id, grant id, exit code, sizes, duration, kill reason and argv[0] only.
/// A callback that throws ends the run's reporting: the process is cleaned up and the exception reaches the caller.
/// </summary>
public sealed class RunExecutor(AgentConfig config, TimeProvider clock, AgentLog log, Func<RunMessage, ExecDecision> guard,
    Func<bool> runningAsRoot)
{
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinTrackEvery = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxTrackEvery = TimeSpan.FromSeconds(5);
    private const int LogValueMax = 200;
    private const string None = "none";

    // zero keeps the tracker off; otherwise bounded so that a mistake cannot make it spin or never look
    private TimeSpan TrackEvery => config.ExecTrackEvery <= TimeSpan.Zero ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Clamp(config.ExecTrackEvery.Ticks, MinTrackEvery.Ticks, MaxTrackEvery.Ticks));

    private int _active;

    private static class KillReasons
    {
        public const string Timeout = "timeout";
        public const string Cancelled = "cancelled";
        public const string OutputLimit = "output_limit";
        public const string Straggler = "straggler";
    }

    public async Task<RunResult> ExecuteAsync(RunMessage run, Func<RunOutputChunk, Task> onChunk, Func<RunStatusUpdate, Task> onStatus,
        CancellationToken cancel)
    {
        var began = clock.GetTimestamp();
        if (runningAsRoot()) return await RefuseAsync(run, RunFailures.RunningAsRoot, null, began, onStatus);
        if (clock.GetUtcNow() > run.NotAfter) return await RefuseAsync(run, RunFailures.TooLate, null, began, onStatus);
        if (Interlocked.Increment(ref _active) > Math.Max(1, config.ExecMaxConcurrent))
        {
            Interlocked.Decrement(ref _active);
            return await RefuseAsync(run, RunFailures.TargetBusy, null, began, onStatus);
        }

        try
        {
            return await RunAsync(run, onChunk, onStatus, began, cancel);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task<RunResult> RunAsync(RunMessage run, Func<RunOutputChunk, Task> onChunk, Func<RunStatusUpdate, Task> onStatus,
        long began, CancellationToken cancel)
    {
        ExecDecision decision;
        try
        {
            decision = guard(run);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write($"run {run.Id}: guard failed ({e.GetType().Name})");
            return await RefuseAsync(run, RunFailures.GuardFailed, null, began, onStatus);
        }

        if (!decision.Allowed) return await RefuseAsync(run, decision.Error ?? RunFailures.BadRun, null, began, onStatus);
        if (cancel.IsCancellationRequested) return await FinishAsync(run, RunStatuses.Cancelled, null, null, decision.ResolvedExe, began, onStatus, KillReasons.Cancelled, null);

        var argv0 = run.Argv is { Count: > 0 } ? run.Argv[0] : null;
        var runnable = run.Mode switch
        {
            RunModes.Argv => argv0 is not null && decision.ResolvedExe is not null,
            RunModes.Shell => !string.IsNullOrEmpty(run.ShellCommand),
            _ => false,
        };
        if (!runnable) return await RefuseAsync(run, RunFailures.BadRun, decision.ResolvedExe, began, onStatus);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(run.TimeoutSeconds, 1, (int)config.ExecTimeoutMax.TotalSeconds));
        log.Write($"run start id={run.Id} grant={GrantOf(run)} mode={run.Mode} exe={LogSafe(argv0 ?? run.Mode)} timeout_s={(int)timeout.TotalSeconds}");

        IRunProcess process;
        try
        {
            process = Start(run, decision, argv0, timeout);
        }
        catch (Exception e) when (e is Win32Exception or IOException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            log.Write($"run {run.Id}: could not start ({e.GetType().Name})"); // the message may hold a path: not logged
            return await FinishAsync(run, RunStatuses.Failed, null, RunFailures.SpawnFailed, decision.ResolvedExe, began, onStatus, None, null);
        }

        await using (process)
        {
            await onStatus(new RunStatusUpdate(RunStatuses.Running, ResolvedExe: decision.ResolvedExe, At: clock.GetUtcNow()));
            return await SuperviseAsync(run, process, decision.ResolvedExe, timeout, began, onChunk, onStatus, cancel);
        }
    }

    private IRunProcess Start(RunMessage run, ExecDecision decision, string? argv0, TimeSpan timeout)
    {
        var env = RunEnvironment.Build(config.Home);
        var limits = RunLimits.For(config, timeout);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(RunEnvironment.TempDir(config.Home));
        if (run.Mode == RunModes.Shell) return ProcessTree.StartShell(run.ShellCommand!, config.Home, env, decision.ResolvedExe, limits, TrackEvery);
        // A grant fixes the working directory; a run that names none under a grant works there (ADR-0005, B4).
        return ProcessTree.Start(decision.ResolvedExe!, run.Argv!.Skip(1).ToList(), run.Cwd ?? run.Grant?.Cwd ?? config.Home, env, argv0, limits, TrackEvery);
    }

    private async Task<RunResult> SuperviseAsync(RunMessage run, IRunProcess process, string? exe, TimeSpan timeout, long began,
        Func<RunOutputChunk, Task> onChunk, Func<RunStatusUpdate, Task> onStatus, CancellationToken cancel)
    {
        var collector = new OutputCollector(config);
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
        var result = new RunResult(status, code, error, collector?.Truncated ?? false, exe, collector?.BytesRead ?? 0, clock.GetElapsedTime(began));
        if (logEnd)
        {
            log.Write($"run end id={run.Id} grant={GrantOf(run)} status={status} exit={code?.ToString() ?? None} read_bytes={result.BytesRead} "
                + $"kept_bytes={collector?.BytesKept ?? 0} truncated={result.OutputTruncated} duration_ms={(long)result.Duration.TotalMilliseconds} kill={kill}");
        }

        await onStatus(result.ToUpdate(clock.GetUtcNow()));
        return result;
    }

    private static string GrantOf(RunMessage run) => run.GrantId?.ToString() ?? None;

    // argv[0] comes from the requester: control characters out (log injection) and a length cap.
    private static string LogSafe(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value.Take(LogValueMax)) sb.Append(char.IsControl(c) ? '?' : c);
        return sb.ToString();
    }
}
