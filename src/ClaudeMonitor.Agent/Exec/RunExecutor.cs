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
public sealed partial class RunExecutor(AgentConfig config, TimeProvider clock, AgentLog log, Func<RunMessage, ExecDecision> guard,
    Func<bool> runningAsRoot)
{
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinTrackEvery = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxTrackEvery = TimeSpan.FromSeconds(5);
    private const int LogValueMax = 200;
    private const string None = "none";

    // zero keeps the tracker off; otherwise bounded so that a mistake cannot make it spin or never look
    private TimeSpan TrackEvery => TrackInterval(config.ExecTrackEvery);

    internal static TimeSpan TrackInterval(TimeSpan configured) => configured <= TimeSpan.Zero ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Clamp(configured.Ticks, MinTrackEvery.Ticks, MaxTrackEvery.Ticks));

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
        if (run.Mode == RunModes.Shell) return ProcessTree.StartShell(run.ShellCommand!, config.Home, env, decision.ResolvedExe, limits, TrackEvery, Note(run));
        // A grant fixes the working directory; a run that names none under a grant works there (ADR-0005, B4).
        return ProcessTree.Start(decision.ResolvedExe!, run.Argv!.Skip(1).ToList(), run.Cwd ?? run.Grant?.Cwd ?? config.Home, env, argv0, limits, TrackEvery, Note(run));
    }

    // What the process layer reports (an error type name, no content) goes to the agent's log under the run's id.
    private Action<string> Note(RunMessage run) => line => log.Write($"run {run.Id}: {line}");


    private static string GrantOf(RunMessage run) => run.GrantId?.ToString() ?? None;

    // argv[0] comes from the requester: control characters out (log injection) and a length cap.
    private static string LogSafe(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value.Take(LogValueMax)) sb.Append(char.IsControl(c) ? '?' : c);
        return sb.ToString();
    }
}
