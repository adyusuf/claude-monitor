using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// `claude update` can be stopped: the daemon's token reaches the run, a stop is recorded as interrupted (not as a failure), and
/// each way a run can fail is told apart in the recorded detail and the log without ever repeating what the process printed.
/// </summary>
public sealed class ClaudeUpdaterStopTests : IDisposable
{
    private const string Secret = "TOP-SECRET-OUTPUT-4711";

    private readonly ClaudeKit kit = new(c => c with { ClaudeUpdateTimeout = TimeSpan.FromMinutes(7) });
    private readonly ScriptedClaudeRunner runner = new();
    private readonly ClaudeUpdater updater;

    public ClaudeUpdaterStopTests()
    {
        updater = Updater(kit);
        kit.AllowAll();
        kit.Idle();
    }

    public void Dispose() => kit.Dispose();

    private ClaudeUpdater Updater(ClaudeKit k) => new(k.Config, k.Store, runner, k.Notifier, k.Log, k.Clock, pid => k.Alive(pid));

    private static string[] Asked(ScriptedClaudeRunner r) => [.. r.Asked];

    // ---- the token reaches the run -----------------------------------------------------------------------------

    [Fact]
    public async Task Stopping_the_agent_while_claude_update_runs_ends_it_and_records_interrupted_with_the_retry_time()
    {
        using var stop = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        runner.OnUpdate = ct =>
        {
            entered.Set();
            return ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)) ? new(ProcessEnd.Cancelled, -1, "") : new(ProcessEnd.Exited, 0, "");
        };

        var run = updater.RunAsync(stop.Token);
        await Until.True(() => entered.IsSet);
        await stop.CancelAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ClaudeCodes.Interrupted, outcome.Code);
        Assert.Contains("the agent stopped while `claude update` ran, so it was ended", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("run `claude update` by hand", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(["--version", "update"], Asked(runner)); // no `--version` after a run that was ended
        Assert.All(runner.Calls, c => Assert.Equal(stop.Token, c.Token)); // the daemon's own token, so its stop reaches the process
        Assert.Equal(ClaudeCodes.Interrupted, kit.State.Result);
        Assert.Equal(0, kit.State.Failures); // a stop is not a fault of `claude update`
        Assert.Equal("2.1.285", kit.State.VersionBefore);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
        Assert.Contains("claude update interrupted:", kit.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_update_gets_the_configured_timeout_and_the_version_probe_a_short_one()
    {
        await updater.RunAsync(CancellationToken.None);
        var calls = runner.Calls.ToArray();
        Assert.Equal(TimeSpan.FromMinutes(7), calls.Single(c => c.Args == "update").Timeout);
        Assert.All(calls.Where(c => c.Args == "--version"), c => Assert.True(c.Timeout < kit.Config.ClaudeUpdateTimeout));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Win32Exception", " (ending it failed: Win32Exception)")]
    public async Task A_cancelled_run_that_could_not_be_killed_says_so_by_type_only(string? error, string told)
    {
        runner.OnUpdate = _ => new(ProcessEnd.Cancelled, -1, Secret, error);
        var outcome = await updater.RunAsync(CancellationToken.None);
        Assert.Equal(ClaudeCodes.Interrupted, outcome.Code);
        Assert.Contains($"so it was ended{told}; if", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, outcome.Detail + kit.LogText + kit.State.Detail, StringComparison.Ordinal);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
    }

    [Fact]
    public async Task A_stop_during_the_version_probe_stops_before_claude_update_starts()
    {
        using var stop = new CancellationTokenSource();
        runner.OnVersion = stop.Cancel;
        var outcome = await updater.RunAsync(stop.Token);

        Assert.Equal(ClaudeCodes.Interrupted, outcome.Code);
        Assert.Contains("the agent stopped before `claude update` started", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(["--version"], Asked(runner));
        Assert.Equal(0, runner.Updates);
        Assert.Equal("2.1.285", kit.State.VersionBefore); // what was learnt before the stop is kept
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
    }

    [Fact]
    public async Task A_token_cancelled_before_a_run_without_countdown_starts_nothing()
    {
        using var zero = new ClaudeKit(c => c with { ClaudeCountdown = TimeSpan.Zero });
        zero.AllowAll();
        zero.Idle();
        var fake = new ScriptedClaudeRunner();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var outcome = await new ClaudeUpdater(zero.Config, zero.Store, fake, zero.Notifier, zero.Log, zero.Clock, zero.Alive).RunAsync(stop.Token);

        Assert.Equal(ClaudeCodes.Interrupted, outcome.Code);
        Assert.Contains("before `claude update` started", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(0, fake.Updates);
        zero.AssertNextAt(zero.Config.UpdateRetryAfter);
    }

    [Fact]
    public async Task A_token_cancelled_before_a_run_with_a_countdown_never_reaches_the_process_runner()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.RunAsync(stop.Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task The_default_runner_overload_never_starts_a_cancelled_run_and_reports_an_exit_otherwise()
    {
        using var stop = new CancellationTokenSource();
        var plain = kit.Runner; // implements only the plain overload, so it takes the interface's default one
        plain.UpdateExit = 5;
        var done = Via(plain, kit.ClaudePath, stop.Token);
        Assert.Equal((ProcessEnd.Exited, 5), (done.End, done.ExitCode));
        await stop.CancelAsync();
        var cancelled = Via(plain, kit.ClaudePath, stop.Token);
        Assert.Equal(ProcessEnd.Cancelled, cancelled.End);
        Assert.Equal(1, plain.Updates); // only the first one started anything
    }

    private static ProcessResult Via<T>(T runner, string file, CancellationToken ct)
        where T : IProcessRunner => runner.Run(file, ["update"], TimeSpan.FromSeconds(1), ct);

    // ---- the failure kinds -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProcessEnd.Exited, 7, null, "`claude update` exited with 7")]
    [InlineData(ProcessEnd.TimedOut, -1, null, "`claude update` did not end within 7 min and was ended")]
    [InlineData(ProcessEnd.TimedOut, -1, "Win32Exception", "did not end within 7 min and was ended (ending it failed: Win32Exception)")]
    [InlineData(ProcessEnd.NotStarted, -1, "Win32Exception", "`claude update` could not be started (Win32Exception)")]
    [InlineData(ProcessEnd.NotStarted, -1, null, "`claude update` could not be started")]
    [InlineData((ProcessEnd)99, -1, null, "ended in a way this agent does not know (99)")]
    public async Task Each_way_a_run_fails_is_told_in_the_detail_and_the_log_without_the_output(ProcessEnd end, int exit, string? error, string told)
    {
        runner.OnUpdate = _ => new(end, exit, Secret, error);
        var outcome = await updater.RunAsync(CancellationToken.None);

        Assert.Equal(ClaudeCodes.Failed, outcome.Code);
        Assert.Contains(told, outcome.Detail, StringComparison.Ordinal);
        Assert.Contains(told, kit.State.Detail, StringComparison.Ordinal);
        Assert.Contains($"claude update failed: {outcome.Detail}", kit.LogText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, kit.State.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, kit.LogText, StringComparison.Ordinal);
        Assert.Equal(1, kit.State.Failures);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
    }

    [Fact]
    public async Task A_start_failure_without_a_type_has_no_empty_parentheses()
    {
        runner.OnUpdate = _ => new(ProcessEnd.NotStarted, -1, "");
        var outcome = await updater.RunAsync(CancellationToken.None);
        Assert.EndsWith("could not be started", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_still_probes_the_version_after_it_so_the_state_shows_what_is_installed()
    {
        runner.OnUpdate = _ => new(ProcessEnd.Exited, 1, "");
        await updater.RunAsync(CancellationToken.None);
        Assert.Equal(["--version", "update", "--version"], Asked(runner));
        Assert.Equal("2.1.285", kit.State.VersionAfter);
    }
}
