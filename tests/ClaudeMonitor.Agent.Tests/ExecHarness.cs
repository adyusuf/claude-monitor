using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A real executor over a throw-away agent home, with real processes and the real guard at exec level shell.</summary>
public sealed class ExecHarness : IDisposable
{
    public ExecHarness(Func<Agent.Config.AgentConfig, Agent.Config.AgentConfig>? tweak = null, Func<bool>? runningAsRoot = null,
        Func<RunMessage, ExecDecision>? guard = null)
    {
        Home = new TempHome(c =>
        {
            var config = c with { ExecKillGrace = TimeSpan.FromMilliseconds(300), FlushEvery = TimeSpan.FromMilliseconds(50) };
            return tweak is null ? config : tweak(config);
        });
        Fx = new ExecFixture();
        Guard = guard ?? (run => ExecGuard.Check(run, ExecFixture.Level(ExecLevels.Shell), ExecFixture.Os, Home.Config.Home));
        Executor = new RunExecutor(Home.Config, TimeProvider.System, new AgentLog(Home.Config, TimeProvider.System),
            run =>
            {
                Interlocked.Increment(ref guardCalls);
                return Guard(run);
            },
            runningAsRoot ?? (() => false));
    }

    public TempHome Home { get; }
    public ExecFixture Fx { get; }
    public RunExecutor Executor { get; }
    public Func<RunMessage, ExecDecision> Guard { get; }
    public int GuardCalls => Volatile.Read(ref guardCalls);
    private int guardCalls;

    public ConcurrentQueue<RunOutputChunk> Chunks { get; } = new();
    public ConcurrentQueue<RunStatusUpdate> Statuses { get; } = new();

    public string Output => string.Concat(Chunks.Select(c => c.Body));

    public string Log => File.Exists(Home.Config.LogPath) ? File.ReadAllText(Home.Config.LogPath) : "";

    public Task<RunResult> Run(RunMessage run, CancellationToken cancel = default) =>
        Executor.ExecuteAsync(run, chunk =>
        {
            Chunks.Enqueue(chunk);
            return Task.CompletedTask;
        }, status =>
        {
            Statuses.Enqueue(status);
            return Task.CompletedTask;
        }, cancel);

    public void Dispose()
    {
        Fx.Dispose();
        Home.Dispose();
    }

    /// <summary>Waits for a condition (polling), failing after the deadline: no fixed sleep.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A shell command that starts a TERM-ignoring child (which keeps ignoring it through exec) and records its pid.</summary>
    public static string StubbornTree(string pidFile) =>
        $"trap '' TERM; sh -c 'echo $$ > {pidFile}; exec sleep 300' & wait";

    public static async Task<int> ReadPidAsync(string pidFile)
    {
        Assert.True(await UntilAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).EndsWith('\n')), "the child never wrote its pid");
        return int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
