using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class RunExecutorKillTests : IDisposable
{
    private readonly ExecHarness h = new();

    public void Dispose() => h.Dispose();

    private string PidFile => Path.Combine(h.Fx.Dir, "child.pid");

    [Fact]
    public async Task A_timeout_ends_a_run_whose_children_ignore_the_polite_signal_and_leaves_no_process_behind()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(ExecFixture.Shell(ExecHarness.StubbornTree(PidFile), timeout: 1));
        var pid = await ExecHarness.ReadPidAsync(PidFile);

        Assert.Equal((RunStatuses.TimedOut, null, null), (result.Status, result.ExitCode, result.Error));
        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived the timeout");
        Assert.True(result.Duration >= TimeSpan.FromSeconds(1), "the timeout is measured from the start");
        Assert.True(result.Duration < TimeSpan.FromSeconds(4), $"the hard kill came too late: {result.Duration}");
        Assert.Equal(RunStatuses.TimedOut, h.Statuses.Last().Status);
        Assert.Single(h.Statuses, s => s.Status is not RunStatuses.Running);
    }

    [Fact]
    public async Task A_cancel_stops_a_running_run_and_its_whole_tree()
    {
        if (!ExecFixture.Unix) return;
        using var cancel = new CancellationTokenSource();
        var run = h.Run(ExecFixture.Shell(ExecHarness.StubbornTree(PidFile), timeout: 600), cancel.Token);
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        Assert.True(ExecHarness.IsAlive(pid));
        await cancel.CancelAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(RunStatuses.Cancelled, result.Status);
        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived the cancel");
    }

    [Fact]
    public async Task A_run_cancelled_before_it_starts_never_starts()
    {
        if (!ExecFixture.Unix) return;
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var result = await h.Run(ExecFixture.Shell($"echo started > {PidFile}"), cancel.Token);
        Assert.Equal(RunStatuses.Cancelled, result.Status);
        Assert.DoesNotContain(h.Statuses, s => s.Status == RunStatuses.Running);
        Assert.False(File.Exists(PidFile));
    }

    [Fact]
    public async Task A_run_that_prints_more_than_the_read_cap_is_killed_as_output_limit_and_marked_truncated()
    {
        if (!ExecFixture.Unix) return;
        using var capped = new ExecHarness(c => c with { RunReadCap = 4096 });
        var result = await capped.Run(ExecFixture.Shell("yes 0123456789", timeout: 60));
        Assert.Equal((RunStatuses.Failed, RunFailures.OutputLimit, true), (result.Status, result.Error, result.OutputTruncated));
        Assert.True(result.BytesRead > 4096);
        Assert.True(result.Duration < TimeSpan.FromSeconds(4), $"the hard kill came too late: {result.Duration}");
        Assert.True(capped.Statuses.Last().OutputTruncated);
    }

    [Fact]
    public async Task A_background_process_that_keeps_the_output_open_after_the_lead_exits_is_killed_and_the_run_still_ends()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(ExecFixture.Shell($"sh -c 'echo $$ > {PidFile}; exec sleep 300' & exit 0", timeout: 60));
        var pid = await ExecHarness.ReadPidAsync(PidFile);
        Assert.Equal(RunStatuses.Succeeded, result.Status);
        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"straggler {pid} survived");
        Assert.Contains("kill=straggler", h.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_prints_slowly_has_its_first_output_sent_before_it_ends()
    {
        if (!ExecFixture.Unix) return;
        using var cancel = new CancellationTokenSource();
        var run = h.Run(ExecFixture.Shell("echo early; sleep 300", timeout: 600), cancel.Token);
        Assert.True(await ExecHarness.UntilAsync(() => h.Output == "early\n"), "the head chunk was never flushed");
        await cancel.CancelAsync();
        Assert.Equal(RunStatuses.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(60))).Status);
    }

    [Fact]
    public async Task A_run_is_refused_as_root_before_anything_is_checked_or_started()
    {
        if (!ExecFixture.Unix) return;
        using var root = new ExecHarness(runningAsRoot: () => true);
        var result = await root.Run(ExecFixture.Argv(["/bin/echo", "x"]));
        Assert.Equal((RunStatuses.Failed, RunFailures.RunningAsRoot), (result.Status, result.Error));
        Assert.Equal(0, root.GuardCalls);
        Assert.Equal("", root.Output);
        Assert.Equal([RunStatuses.Failed], root.Statuses.Select(s => s.Status));
    }

    [Fact]
    public async Task A_run_whose_not_after_has_passed_is_too_late_and_one_at_the_limit_still_starts()
    {
        if (!ExecFixture.Unix) return;
        var late = ExecFixture.Argv(["/bin/echo", "x"]) with { NotAfter = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var result = await h.Run(late);
        Assert.Equal((RunStatuses.Failed, RunFailures.TooLate), (result.Status, result.Error));
        Assert.Equal(0, h.GuardCalls);

        var fresh = await h.Run(ExecFixture.Argv(["/bin/echo", "x"]) with { NotAfter = DateTimeOffset.UtcNow.AddSeconds(30) });
        Assert.Equal(RunStatuses.Succeeded, fresh.Status);
    }

    [Fact]
    public async Task A_third_concurrent_run_is_refused_as_busy_and_a_slot_comes_back_when_a_run_ends()
    {
        if (!ExecFixture.Unix) return;
        using var cancel = new CancellationTokenSource();
        var first = h.Run(ExecFixture.Shell("sleep 300", timeout: 600), cancel.Token);
        var second = h.Run(ExecFixture.Shell("sleep 300", timeout: 600), cancel.Token);
        Assert.True(await ExecHarness.UntilAsync(() => h.Statuses.Count(s => s.Status == RunStatuses.Running) == 2), "two runs never reached running");

        var third = await h.Run(ExecFixture.Argv(["/bin/echo", "x"]));
        Assert.Equal((RunStatuses.Failed, RunFailures.TargetBusy), (third.Status, third.Error));
        Assert.Equal(2, h.GuardCalls); // refused before the guard looked at it

        await cancel.CancelAsync();
        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60)), r => Assert.Equal(RunStatuses.Cancelled, r.Status));
        Assert.Equal(RunStatuses.Succeeded, (await h.Run(ExecFixture.Argv(["/bin/echo", "x"]))).Status);
    }

    [Fact]
    public async Task A_refusal_by_the_guard_is_the_runs_error_and_nothing_is_started()
    {
        if (!ExecFixture.Unix) return;
        using var refused = new ExecHarness(guard: _ => ExecDecision.Refuse(ExecErrors.PathEscape));
        var result = await refused.Run(ExecFixture.Argv(["/bin/echo", "x"]));
        Assert.Equal((RunStatuses.Failed, ExecErrors.PathEscape), (result.Status, result.Error));
        Assert.Equal([RunStatuses.Failed], refused.Statuses.Select(s => s.Status));
        Assert.Contains("run refused", refused.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_guard_that_cannot_read_the_file_system_fails_the_run_as_guard_failed()
    {
        if (!ExecFixture.Unix) return;
        using var broken = new ExecHarness(guard: _ => throw new IOException("disk"));
        var result = await broken.Run(ExecFixture.Argv(["/bin/echo", "x"]));
        Assert.Equal((RunStatuses.Failed, RunFailures.GuardFailed), (result.Status, result.Error));
    }

    [Fact]
    public async Task An_allowed_argv_decision_without_a_resolved_program_is_a_bad_run()
    {
        if (!ExecFixture.Unix) return;
        using var odd = new ExecHarness(guard: _ => new ExecDecision(true, null, null));
        var result = await odd.Run(ExecFixture.Argv(["/bin/echo", "x"]));
        Assert.Equal((RunStatuses.Failed, RunFailures.BadRun), (result.Status, result.Error));
    }
}
