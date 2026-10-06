using System.Diagnostics;
using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// Real processes: a descendant that leaves the process group with setsid() is ended by a kill on macOS (ADR-0005). Linux
/// has the service cgroup instead and no tracker, so these return there; the tracker's logic is in DescendantTrackerTests.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class RunTrackingMacTests : IDisposable
{
    private const string Perl = "/usr/bin/perl";
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(50);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cm-track-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> cleanup = [];

    public RunTrackingMacTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        foreach (var pid in cleanup.Where(ExecHarness.IsAlive)) Process.GetProcessById(pid).Kill();
        Directory.Delete(dir, recursive: true);
    }

    private static bool Available => OperatingSystem.IsMacOS() && File.Exists(Perl);

    private string PidFile => Path.Combine(dir, "escaped.pid");

    // The lead (perl) forks a child that starts a session of its own, writes its pid and sleeps; the lead sleeps too.
    private IRunProcess StartEscaper(TimeSpan trackEvery) => ProcessTree.Start(Perl,
        ["-e", "use POSIX; if (fork() == 0) { POSIX::setsid(); open(F, \">$ARGV[0]\"); print F \"$$\\n\"; close F; sleep 300; exit 0 } sleep 300", PidFile],
        dir, Env, trackEvery: trackEvery);

    [Fact]
    public async Task Without_the_tracker_a_process_that_leaves_the_group_survives_a_kill_which_shows_the_other_tests_mean_something()
    {
        if (!Available) return;
        await using var run = StartEscaper(TimeSpan.Zero);
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        cleanup.Add(pid);

        run.KillNow();
        await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(ExecHarness.IsAlive(pid), "the escaped process died without the tracker: the setsid fixture does not escape");
    }

    [Fact]
    public async Task KillNow_ends_a_descendant_that_left_the_process_group()
    {
        if (!Available) return;
        await using var run = StartEscaper(Fast);
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        cleanup.Add(pid);
        Assert.True(ExecHarness.IsAlive(pid));

        run.KillNow();

        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived KillNow");
        await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Kill_asks_a_descendant_that_left_the_process_group_to_stop_too()
    {
        if (!Available) return;
        await using var run = StartEscaper(Fast);
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        cleanup.Add(pid);

        run.Kill();

        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived SIGTERM");
    }

    [Fact]
    public async Task Disposing_a_run_ends_what_escaped_even_when_nobody_asked_for_a_kill()
    {
        if (!Available) return;
        var run = StartEscaper(Fast);
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        cleanup.Add(pid);

        await run.DisposeAsync();

        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived the disposal");
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(0, false)] // the configuration can turn the tracker off
    public async Task The_executor_ends_an_escaped_process_when_the_run_times_out_unless_the_tracker_is_off(int everyMs, bool ended)
    {
        if (!Available) return;
        using var h = new ExecHarness(c => c with { ExecTrackEvery = TimeSpan.FromMilliseconds(everyMs) });
        var script = "/usr/bin/perl -e 'use POSIX; if (fork() == 0) { POSIX::setsid(); open(F, \">" + PidFile + "\"); print F \"$$\\n\"; close F; sleep 300; exit 0 } sleep 300'";
        var run = h.Run(ExecFixture.Shell(script, timeout: 1));
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        cleanup.Add(pid);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(RunStatuses.TimedOut, result.Status);
        if (ended) Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived the timeout");
        else Assert.True(ExecHarness.IsAlive(pid), "the tracker ran although it was turned off");
    }

    [Fact]
    public async Task The_process_table_reports_the_pid_the_parent_and_the_start_time_of_real_processes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var table = new LibProcTable();
        var self = Environment.ProcessId;
        var me = table.Find(self);
        Assert.NotNull(me);
        Assert.Equal(self, me.Pid);
        var started = DateTimeOffset.FromUnixTimeMilliseconds(me.StartMicros / 1000);
        Assert.InRange((started - new DateTimeOffset(Process.GetCurrentProcess().StartTime)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));

        using var child = Process.Start(new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false })!;
        try
        {
            var found = table.ChildrenOf(self).SingleOrDefault(c => c.Pid == child.Id);
            Assert.NotNull(found);
            Assert.Equal(self, found.ParentPid);
            Assert.Equal(table.Find(child.Id)!.StartMicros, found.StartMicros);
            Assert.Empty(table.ChildrenOf(child.Id));
        }
        finally
        {
            child.Kill();
            await child.WaitForExitAsync();
        }

        Assert.Null(table.Find(child.Id));
    }
}
