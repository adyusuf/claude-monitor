using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Daemon;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The Claude updater when everything allows it: announce, count down, run `claude update` and nothing else, remember the result.</summary>
public sealed class ClaudeUpdaterRunTests : IDisposable
{
    private const string Before = "2.1.285 (Claude Code)";
    private const string After = "2.1.291 (Claude Code)";

    private readonly ClaudeKit kit = new();

    public ClaudeUpdaterRunTests()
    {
        kit.AllowAll();
        kit.Idle();
    }

    public void Dispose() => kit.Dispose();

    private string[] Asked => [.. kit.Runner.Asked];

    [Fact]
    public async Task All_idle_announces_counts_down_then_runs_version_update_version_in_that_order_and_remembers_it()
    {
        kit.Runner.Versions(Before, After);
        TimeSpan? elapsedAtUpdate = null;
        kit.Runner.OnUpdate = () => elapsedAtUpdate = kit.Clock.Elapsed;
        string? duringCountdown = null;
        var callsAtNotice = -1;
        kit.Notifier.OnNotify = _ =>
        {
            duringCountdown = kit.State.CountdownUntil;
            callsAtNotice = kit.Runner.Calls.Count;
        };

        var outcome = await kit.RunAsync();

        Assert.Equal(ClaudeCodes.Updated, outcome.Code);
        Assert.Equal(["--version", "update", "--version"], Asked);
        Assert.Equal(0, callsAtNotice); // nothing ran before the notice
        Assert.True(elapsedAtUpdate >= kit.Config.ClaudeCountdown, $"the update started after {elapsedAtUpdate}, before the countdown ran out");
        Assert.Equal(Start.Add(kit.Config.ClaudeCountdown), DateTimeOffset.Parse(duringCountdown!, null, System.Globalization.DateTimeStyles.RoundtripKind));

        var message = Assert.Single(kit.Notifier.Messages);
        Assert.Contains("cm-agent claude-update cancel", message, StringComparison.Ordinal);
        Assert.Contains("1 min", message, StringComparison.Ordinal);

        var state = kit.State;
        Assert.Equal(ClaudeCodes.Updated, state.Result);
        Assert.Equal(("2.1.285", "2.1.291"), (state.VersionBefore, state.VersionAfter));
        Assert.Null(state.CountdownUntil);
        Assert.NotNull(state.CheckedAt);
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
        Assert.Contains("2.1.285 -> 2.1.291", state.Detail, StringComparison.Ordinal);
        Assert.Contains("keep their version", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("will be updated in 1 min", kit.LogText, StringComparison.Ordinal);
        Assert.Contains("claude update updated: Claude Code 2.1.285 -> 2.1.291", kit.LogText, StringComparison.Ordinal);
        Assert.False(File.Exists(kit.Config.ClaudeCancelPath));
    }

    private static DateTimeOffset Start => ClaudeKit.Start;

    [Theory]
    [InlineData(3, 1)]
    [InlineData(60, 1)]
    [InlineData(90, 2)]
    [InlineData(300, 5)]
    public async Task The_notice_names_the_minutes_of_the_countdown_rounded_up(int seconds, int minutes)
    {
        using var other = new ClaudeKit(c => c with { ClaudeCountdown = TimeSpan.FromSeconds(seconds), ClaudeCountdownPoll = TimeSpan.FromSeconds(30) });
        other.AllowAll();
        other.Idle();
        await other.RunAsync();
        Assert.Contains($"in {minutes} min.", Assert.Single(other.Notifier.Messages), StringComparison.Ordinal);
        Assert.Equal(1, other.Runner.Updates);
        Assert.True(other.Clock.Elapsed >= TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public async Task The_same_version_before_and_after_is_unchanged_and_the_next_attempt_waits_the_usual_time()
    {
        kit.Runner.Versions(After, After);
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Unchanged, outcome.Code);
        Assert.Equal(["--version", "update", "--version"], Asked);
        Assert.Equal(ClaudeCodes.Unchanged, kit.State.Result);
        Assert.Equal(("2.1.291", "2.1.291"), (kit.State.VersionBefore, kit.State.VersionAfter));
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
        Assert.Contains("up to date (2.1.291)", kit.State.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_update_is_failed_with_the_exit_code_and_is_retried_after_the_retry_time_not_the_usual_one()
    {
        kit.Runner.UpdateExit = 7;
        kit.Runner.Versions(Before, After); // even if the version changed, a non-zero exit is a failure
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Failed, outcome.Code);
        Assert.Contains("exited with 7", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(ClaudeCodes.Failed, kit.State.Result);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
        Assert.NotEqual(kit.Config.UpdateRetryAfter, kit.Config.ClaudeUpdateEvery);
        Assert.Equal(("2.1.285", "2.1.291"), (kit.State.VersionBefore, kit.State.VersionAfter));
        Assert.Contains("claude update failed", kit.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_update_with_the_same_version_is_failed_not_unchanged()
    {
        kit.Runner.UpdateExit = 1;
        kit.Runner.Versions(Before, Before);
        Assert.Equal(ClaudeCodes.Failed, (await kit.RunAsync()).Code);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
    }

    [Fact]
    public async Task A_version_output_without_numbers_gives_no_versions_but_the_exit_code_still_decides()
    {
        kit.Runner.Versions("claude: command crashed", "???");
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Unchanged, outcome.Code);
        Assert.Equal(["--version", "update", "--version"], Asked);
        Assert.Null(kit.State.VersionBefore);
        Assert.Null(kit.State.VersionAfter);
        Assert.Contains("version unknown", kit.State.Detail, StringComparison.Ordinal);
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
    }

    [Fact]
    public async Task A_version_that_could_not_be_read_before_but_can_after_counts_as_updated()
    {
        kit.Runner.VersionFails();
        kit.Runner.Versions(After);
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Updated, outcome.Code);
        Assert.Null(kit.State.VersionBefore);
        Assert.Equal("2.1.291", kit.State.VersionAfter);
        Assert.Contains("? -> 2.1.291", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_call_before_the_next_attempt_is_due_does_nothing_and_a_call_after_it_runs_again()
    {
        kit.Runner.Versions(Before, After, After, After);
        await kit.RunAsync();
        var calls = kit.Runner.Calls.Count;
        var stateBefore = kit.State;

        var again = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.NotDue, again.Code);
        Assert.Equal(calls, kit.Runner.Calls.Count);
        Assert.Single(kit.Notifier.Messages);
        Assert.Equal(stateBefore, kit.State);

        kit.Clock.Advance(kit.Config.ClaudeUpdateEvery + TimeSpan.FromMinutes(1));
        kit.Idle(); // the session file is as old as it was, so still idle
        Assert.Equal(ClaudeCodes.Unchanged, (await kit.RunAsync()).Code);
        Assert.Equal(2, kit.Runner.Updates);
        Assert.Equal(2, kit.Notifier.Messages.Count);
    }

    [Fact]
    public async Task A_session_whose_process_is_gone_does_not_hold_the_update_back()
    {
        kit.Files.Session(200, "busy", kit.Now.AddMinutes(-1));
        kit.Alive = pid => pid != 200;
        Assert.Equal(ClaudeCodes.Unchanged, (await kit.RunAsync()).Code);
        Assert.Equal(1, kit.Runner.Updates);
    }

    [Fact]
    public async Task No_session_at_all_is_idle_and_the_update_goes_ahead()
    {
        Directory.Delete(kit.Files.Folder, recursive: true);
        kit.Files.Write("readme.txt", "x"); // the folder exists, with no session in it
        Assert.Equal(ClaudeCodes.Unchanged, (await kit.RunAsync()).Code);
        Assert.Equal(1, kit.Runner.Updates);
    }

    [Fact]
    public async Task Two_attempts_at_once_the_second_is_busy_and_does_nothing()
    {
        using var held = DaemonHost.TryLock(kit.Config.ClaudeUpdateLockPath);
        Assert.NotNull(held);
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Busy, outcome.Code);
        kit.AssertNothingHappened();
        Assert.Null(kit.State.Result);
    }

    [Fact]
    public async Task Without_a_desktop_notification_the_log_says_so_and_the_countdown_still_runs()
    {
        kit.Notifier.Result = false;
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Unchanged, outcome.Code);
        Assert.Single(kit.Notifier.Messages);
        Assert.Contains("no desktop notification on this system", kit.LogText, StringComparison.Ordinal);
        Assert.Contains("will be updated in 1 min", kit.LogText, StringComparison.Ordinal);
        Assert.True(kit.Clock.Elapsed >= kit.Config.ClaudeCountdown);
        Assert.Equal(1, kit.Runner.Updates);
    }

    [Fact]
    public async Task When_the_notice_was_shown_the_log_does_not_claim_there_was_none()
    {
        await kit.RunAsync();
        Assert.DoesNotContain("no desktop notification", kit.LogText, StringComparison.Ordinal);
    }
}
