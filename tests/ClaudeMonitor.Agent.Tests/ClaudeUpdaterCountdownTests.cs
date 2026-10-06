using System.Globalization;
using ClaudeMonitor.Agent.ClaudeUpdate;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>What stops a Claude Code update while its countdown runs: a cancel, a session that is no longer idle, consent taken back.</summary>
public sealed class ClaudeUpdaterCountdownTests : IDisposable
{
    private readonly ClaudeKit kit = new();

    public ClaudeUpdaterCountdownTests()
    {
        kit.AllowAll();
        kit.Idle();
        kit.Runner.Versions("2.1.285 (Claude Code)", "2.1.291 (Claude Code)");
    }

    public void Dispose() => kit.Dispose();

    private void AssertStoppedBeforeUpdating(string code)
    {
        Assert.Equal(0, kit.Runner.Updates);
        Assert.Empty(kit.Runner.Calls); // not even `--version`
        Assert.Equal(code, kit.State.Result);
        Assert.Null(kit.State.CountdownUntil);
        Assert.Single(kit.Notifier.Messages); // announced once, never repeated
    }

    [Fact]
    public async Task A_cancel_that_arrives_during_the_countdown_cancels_the_update_and_snoozes_it()
    {
        kit.Notifier.OnNotify = _ => File.WriteAllText(kit.Config.ClaudeCancelPath, "x");
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Cancelled, outcome.Code);
        AssertStoppedBeforeUpdating(ClaudeCodes.Cancelled);
        Assert.False(File.Exists(kit.Config.ClaudeCancelPath), "the cancel is used up");
        kit.AssertNextAt(kit.Config.ClaudeSnooze);
        Assert.Contains("claude update cancelled", kit.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancel_written_by_the_command_a_person_runs_cancels_it_too()
    {
        var exit = -1;
        var told = new StringWriter();
        kit.Clock.OnTick = n =>
        {
            if (n != 2) return;
            exit = Task.Run(() => ClaudeUpdateCommand.RunAsync(["claude-update", "cancel"], kit.Config, told, new StringWriter(), kit.Clock)).GetAwaiter().GetResult();
        };
        Assert.Equal(ClaudeCodes.Cancelled, (await kit.RunAsync()).Code);
        Assert.Equal(0, exit);
        Assert.Contains("cancelled", told.ToString(), StringComparison.Ordinal);
        AssertStoppedBeforeUpdating(ClaudeCodes.Cancelled);
        Assert.False(File.Exists(kit.Config.ClaudeCancelPath));
        kit.AssertNextAt(kit.Config.ClaudeSnooze);
    }

    [Fact]
    public async Task A_cancel_file_left_over_from_before_the_countdown_does_not_cancel_and_is_removed()
    {
        File.WriteAllText(kit.Config.ClaudeCancelPath, "stale");
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Updated, outcome.Code);
        Assert.Equal(["--version", "update", "--version"], kit.Runner.Asked);
        Assert.False(File.Exists(kit.Config.ClaudeCancelPath));
    }

    [Fact]
    public async Task A_session_that_turns_busy_during_the_countdown_stops_it_without_updating_and_clears_the_next_attempt()
    {
        Prepare_a_next_attempt_in_the_past();
        kit.Clock.OnTick = n =>
        {
            if (n == 2) kit.Files.Session(100, "busy", kit.Now);
        };
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("stopped: a session is busy", outcome.Detail, StringComparison.Ordinal);
        AssertStoppedBeforeUpdating(ClaudeCodes.Waiting);
        Assert.Null(kit.State.NextAt);
        Assert.Contains("claude update waiting: stopped", kit.LogText, StringComparison.Ordinal);
        Assert.True(kit.Clock.Ticks < 3, "it stopped when the session turned busy, not at the end");
    }

    [Fact]
    public async Task A_session_that_was_active_again_during_the_countdown_stops_it_even_though_its_status_says_idle()
    {
        kit.Clock.OnTick = n =>
        {
            if (n == 1) kit.Files.Session(100, "idle", kit.Now); // idle again, but only just
        };
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("less than 10 min", outcome.Detail, StringComparison.Ordinal);
        AssertStoppedBeforeUpdating(ClaudeCodes.Waiting);
    }

    [Fact]
    public async Task A_new_session_starting_during_the_countdown_stops_it()
    {
        kit.Clock.OnTick = n =>
        {
            if (n == 1) kit.Files.Session(300, "busy", kit.Now);
        };
        Assert.Equal(ClaudeCodes.Waiting, (await kit.RunAsync()).Code);
        AssertStoppedBeforeUpdating(ClaudeCodes.Waiting);
    }

    [Fact]
    public async Task A_session_file_that_becomes_unreadable_during_the_countdown_stops_it()
    {
        kit.Clock.OnTick = n =>
        {
            if (n == 1) kit.Files.Write("100.json", "{ torn write");
        };
        Assert.Equal(ClaudeCodes.Waiting, (await kit.RunAsync()).Code);
        AssertStoppedBeforeUpdating(ClaudeCodes.Waiting);
    }

    [Theory]
    [InlineData(false, "true")]
    [InlineData(true, "false")]
    [InlineData(false, "false")]
    public async Task Consent_taken_back_during_the_countdown_by_either_side_stops_it_and_nothing_runs(bool machine, string workspace)
    {
        kit.Clock.OnTick = n =>
        {
            if (n == 1) kit.Consent(machine, workspace);
        };
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Disabled, outcome.Code);
        AssertStoppedBeforeUpdating(ClaudeCodes.Disabled);
        Assert.Contains("switched off during the countdown", kit.State.Detail, StringComparison.Ordinal);
        Assert.Contains("claude update disabled", kit.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_countdown_is_announced_again_by_the_next_attempt_which_may_then_run()
    {
        kit.Clock.OnTick = n =>
        {
            if (n == 1) kit.Files.Session(100, "busy", kit.Now);
        };
        Assert.Equal(ClaudeCodes.Waiting, (await kit.RunAsync()).Code);
        kit.Clock.OnTick = null;
        kit.Idle(since: TimeSpan.FromMinutes(20));
        Assert.Equal(ClaudeCodes.Updated, (await kit.RunAsync()).Code); // nothing was scheduled by the stopped one
        Assert.Equal(2, kit.Notifier.Messages.Count);
        Assert.Equal(1, kit.Runner.Updates);
    }

    private void Prepare_a_next_attempt_in_the_past() =>
        ClaudeUpdateState.Change(kit.Config, s => s with { NextAt = kit.Now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture) });
}
