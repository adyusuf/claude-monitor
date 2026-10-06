using System.Globalization;
using ClaudeMonitor.Agent;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The very end of a countdown, a countdown that an attempt no longer owns, and the cancel message.</summary>
public sealed class ClaudeUpdaterEdgeTests : IDisposable
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 6, 14, 30, 0, TimeSpan.Zero);

    private readonly ClaudeKit kit = new();

    public ClaudeUpdaterEdgeTests()
    {
        kit.AllowAll();
        kit.Idle();
        kit.Runner.Versions("2.1.285 (Claude Code)", "2.1.291 (Claude Code)");
    }

    public void Dispose() => kit.Dispose();

    /// <summary>Runs with something changing in the very last wait of the countdown (3 s long, polled every second: the 3rd wait is the last).</summary>
    private async Task<ClaudeOutcome> RunChangingAtTheLastTick(Action change)
    {
        kit.Clock.OnTick = n =>
        {
            if (n == 3) change();
        };
        var outcome = await kit.RunAsync();
        Assert.Equal(3, kit.Clock.Ticks); // it really was the last wait, and no further one was started
        Assert.Equal(0, kit.Runner.Updates);
        Assert.Empty(kit.Runner.Calls);
        Assert.Null(kit.State.CountdownUntil);
        return outcome;
    }

    // ---- the last tick -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancel_that_appears_only_in_the_last_tick_still_cancels()
    {
        var outcome = await RunChangingAtTheLastTick(() => File.WriteAllText(kit.Config.ClaudeCancelPath, "x"));
        Assert.Equal(ClaudeCodes.Cancelled, outcome.Code);
        Assert.False(File.Exists(kit.Config.ClaudeCancelPath));
        kit.AssertNextAt(kit.Config.ClaudeSnooze);
    }

    [Fact]
    public async Task A_session_turning_busy_in_the_last_tick_still_stops_the_update()
    {
        var outcome = await RunChangingAtTheLastTick(() => kit.Files.Session(100, "busy", kit.Now));
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("a session is busy", outcome.Detail, StringComparison.Ordinal);
        Assert.Null(kit.State.NextAt);
    }

    [Fact]
    public async Task A_new_session_appearing_in_the_last_tick_still_stops_the_update()
    {
        var outcome = await RunChangingAtTheLastTick(() => kit.Files.Session(300, "idle", kit.Now));
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code); // idle, but only just
    }

    [Theory]
    [InlineData(false, "true")]
    [InlineData(true, "false")]
    public async Task Consent_withdrawn_in_the_last_tick_still_stops_the_update(bool machine, string workspace)
    {
        var outcome = await RunChangingAtTheLastTick(() => kit.Consent(machine, workspace));
        Assert.Equal(ClaudeCodes.Disabled, outcome.Code);
        Assert.Equal(ClaudeCodes.Disabled, kit.State.Result);
    }

    [Fact]
    public async Task When_nothing_changes_in_the_last_tick_the_update_runs_after_the_full_countdown()
    {
        kit.Clock.OnTick = null;
        Assert.Equal(ClaudeCodes.Updated, (await kit.RunAsync()).Code);
        Assert.Equal(3, kit.Clock.Ticks);
        Assert.True(kit.Clock.Elapsed >= kit.Config.ClaudeCountdown);
        Assert.Equal(1, kit.Runner.Updates);
    }

    [Fact]
    public async Task A_countdown_of_zero_still_checks_once_before_updating()
    {
        using var zero = new ClaudeKit(c => c with { ClaudeCountdown = TimeSpan.Zero });
        zero.AllowAll();
        zero.Idle();
        File.WriteAllText(zero.Config.ClaudeCancelPath, "x"); // stale: removed at the start, so it does not count
        Assert.Equal(ClaudeCodes.Unchanged, (await zero.RunAsync()).Code);
        zero.Files.Session(100, "busy", zero.Now);
        zero.Clock.Advance(zero.Config.ClaudeUpdateEvery + TimeSpan.FromMinutes(1));
        Assert.Equal(ClaudeCodes.Waiting, (await zero.RunAsync()).Code);
        Assert.Equal(1, zero.Runner.Updates);
    }

    // ---- an interrupted countdown ------------------------------------------------------------------------------

    private void LeaveCountdownBehind() =>
        ClaudeUpdateState.Change(kit.Config, _ => new ClaudeUpdateState(CheckedAt: kit.Now.ToString("O", CultureInfo.InvariantCulture), Result: "countdown",
            Detail: "Claude Code will be updated when the countdown ends", CountdownUntil: kit.Now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture)));

    [Fact]
    public async Task A_countdown_found_with_nobody_holding_the_lock_is_cleared_even_when_the_updater_is_disabled()
    {
        kit.Consent(machine: false, workspace: "false");
        LeaveCountdownBehind();
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Disabled, outcome.Code);
        kit.AssertNothingHappened();
        var state = kit.State;
        Assert.Null(state.CountdownUntil);
        Assert.Equal("interrupted", state.Result);
        Assert.Contains("agent was stopped", state.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_countdown_found_with_nobody_holding_the_lock_is_cleared_and_the_updater_then_carries_on()
    {
        LeaveCountdownBehind();
        Assert.Equal(ClaudeCodes.Updated, (await kit.RunAsync()).Code);
        Assert.Null(kit.State.CountdownUntil);
        Assert.Equal(1, kit.Runner.Updates);
    }

    [Fact]
    public async Task A_countdown_that_another_holder_of_the_lock_owns_is_left_alone()
    {
        kit.Consent(machine: false, workspace: "false");
        LeaveCountdownBehind();
        var before = kit.State;
        using var held = DaemonHost.TryLock(kit.Config.ClaudeUpdateLockPath);
        Assert.NotNull(held);
        Assert.Equal(ClaudeCodes.Disabled, (await kit.RunAsync()).Code);
        Assert.Equal(before, kit.State);
        kit.AssertNothingHappened();
    }

    [Fact]
    public async Task A_state_without_a_countdown_is_not_touched_by_the_resolve()
    {
        kit.Consent(machine: false, workspace: "false");
        ClaudeUpdateState.Change(kit.Config, _ => new ClaudeUpdateState(Result: ClaudeCodes.Updated, Detail: "x"));
        await kit.RunAsync();
        Assert.Equal((ClaudeCodes.Updated, "x"), (kit.State.Result, kit.State.Detail));
    }

    [Fact]
    public async Task Status_no_longer_promises_an_update_after_the_countdown_was_resolved()
    {
        kit.Consent(machine: true, workspace: "true");
        LeaveCountdownBehind();
        string[] Lines()
        {
            var (stdout, stderr) = (new StringWriter(), new StringWriter());
            Cli.RunAsync(["status"], kit.Config, new StringReader(""), stdout, stderr, kit.Clock).GetAwaiter().GetResult();
            return stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }

        Assert.Contains(await Task.Run(Lines), l => l.Contains("will be updated at", StringComparison.Ordinal)); // before: it still says so
        kit.Consent(machine: false, workspace: "true");
        await kit.RunAsync(); // the daemon's next poll
        kit.Consent(machine: true, workspace: "true");
        var after = await Task.Run(Lines);
        Assert.DoesNotContain(after, l => l.Contains("will be updated at", StringComparison.Ordinal));
        Assert.Contains(after, l => l.StartsWith("last Claude Code update: interrupted", StringComparison.Ordinal));
    }

    // ---- the cancel message ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(6, "6 h")]
    [InlineData(24, "24 h")]
    [InlineData(1, "1 h")]
    public async Task The_cancel_message_names_the_snooze_from_the_configuration(int hours, string expected)
    {
        var config = kit.Config with { ClaudeSnooze = TimeSpan.FromHours(hours) };
        ClaudeUpdateState.Change(config, _ => new ClaudeUpdateState(CountdownUntil: Fixed.AddMinutes(3).ToString("O", CultureInfo.InvariantCulture)));
        var stdout = new StringWriter();
        var code = await ClaudeUpdateCommand.RunAsync(["claude-update", "cancel"], config, stdout, new StringWriter(), new ManualClock(Fixed));
        Assert.Equal(0, code);
        Assert.Contains($"not tried again for {expected}", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("a day", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(config.ClaudeCancelPath));
    }
}
