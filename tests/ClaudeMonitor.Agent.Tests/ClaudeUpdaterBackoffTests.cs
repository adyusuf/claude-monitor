using System.Globalization;
using ClaudeMonitor.Agent.ClaudeUpdate;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Failures in a row: each is retried after the retry time, the third (by default) only after the usual interval; a success starts over.</summary>
public sealed class ClaudeUpdaterBackoffTests : IDisposable
{
    private readonly ClaudeKit kit = new();

    public ClaudeUpdaterBackoffTests()
    {
        kit.AllowAll();
        kit.Idle();
    }

    public void Dispose() => kit.Dispose();

    private void AdvancePastRetry() => kit.Clock.Advance(kit.Config.UpdateRetryAfter + TimeSpan.FromMinutes(1));

    [Fact]
    public async Task Two_failures_are_retried_after_the_retry_time_and_the_third_waits_the_usual_interval()
    {
        kit.Runner.UpdateExit = 1;

        var first = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Failed, first.Code);
        Assert.DoesNotContain("in a row", first.Detail, StringComparison.Ordinal);
        Assert.Equal(1, kit.State.Failures);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);

        AdvancePastRetry();
        var second = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Failed, second.Code);
        Assert.DoesNotContain("in a row", second.Detail, StringComparison.Ordinal);
        Assert.Equal(2, kit.State.Failures);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);

        AdvancePastRetry();
        var third = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Failed, third.Code);
        Assert.Contains("3 failures in a row, so the next attempt waits 24 h", third.Detail, StringComparison.Ordinal);
        Assert.Equal(3, kit.State.Failures);
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);

        // the retry time no longer brings another attempt; the usual interval does
        AdvancePastRetry();
        Assert.Equal(ClaudeCodes.NotDue, (await kit.RunAsync()).Code);
        Assert.Equal(3, kit.Runner.Updates);
        kit.Clock.Advance(kit.Config.ClaudeUpdateEvery);
        Assert.Equal(ClaudeCodes.Failed, (await kit.RunAsync()).Code);
        Assert.Equal((4, 4), (kit.Runner.Updates, kit.State.Failures));
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
    }

    [Fact]
    public async Task The_number_of_failures_before_the_back_off_comes_from_the_configuration()
    {
        using var one = new ClaudeKit(c => c with { ClaudeFailuresBeforeBackoff = 1 });
        one.AllowAll();
        one.Idle();
        one.Runner.UpdateExit = 1;
        Assert.Equal(ClaudeCodes.Failed, (await one.RunAsync()).Code);
        one.AssertNextAt(one.Config.ClaudeUpdateEvery);
    }

    [Fact]
    public async Task A_failure_counts_on_from_the_failures_already_remembered()
    {
        ClaudeUpdateState.Change(kit.Config, s => s with { Failures = 2 });
        kit.Runner.UpdateExit = 3;
        await kit.RunAsync();
        Assert.Equal(3, kit.State.Failures);
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
    }

    [Theory]
    [InlineData("2.1.285 (Claude Code)", "2.1.291 (Claude Code)", ClaudeCodes.Updated)]
    [InlineData("2.1.291 (Claude Code)", "2.1.291 (Claude Code)", ClaudeCodes.Unchanged)]
    public async Task A_success_starts_the_count_over(string before, string after, string result)
    {
        ClaudeUpdateState.Change(kit.Config, s => s with { Failures = 2 });
        kit.Runner.Versions(before, after);
        Assert.Equal(result, (await kit.RunAsync()).Code);
        Assert.Equal(0, kit.State.Failures);

        // and the next failure is the first one again: retried after the retry time
        kit.Clock.Advance(kit.Config.ClaudeUpdateEvery + TimeSpan.FromMinutes(1));
        kit.Runner.UpdateExit = 1;
        await kit.RunAsync();
        Assert.Equal(1, kit.State.Failures);
        kit.AssertNextAt(kit.Config.UpdateRetryAfter);
    }

    [Fact]
    public async Task A_run_that_was_stopped_keeps_the_failures_as_they_were()
    {
        ClaudeUpdateState.Change(kit.Config, s => s with { Failures = 2 });
        using var stop = new CancellationTokenSource();
        var fake = new ScriptedClaudeRunner { OnVersion = stop.Cancel };
        var outcome = await new ClaudeUpdater(kit.Config, kit.Store, fake, kit.Notifier, kit.Log, kit.Clock, kit.Alive).RunAsync(stop.Token);
        Assert.Equal(ClaudeCodes.Interrupted, outcome.Code);
        Assert.Equal(2, kit.State.Failures);
    }

    // ---- what `cm-agent status` shows --------------------------------------------------------------------------

    private List<string> StatusWith(int failures)
    {
        ClaudeUpdateState.Change(kit.Config, _ => new ClaudeUpdateState(CheckedAt: kit.Now.ToString("O", CultureInfo.InvariantCulture),
            Result: ClaudeCodes.Failed, Detail: "`claude update` exited with 1", Failures: failures));
        return [.. ClaudeUpdateCommand.Describe(kit.Config, kit.Store, kit.Clock)];
    }

    [Fact]
    public void Status_says_when_the_update_failed_enough_times_in_a_row_to_be_backed_off()
    {
        var lines = StatusWith(3);
        Assert.Contains("Claude Code update failed 3 times in a row: it is tried only every 24 h until it works", lines);
        Assert.Contains(lines, l => l.StartsWith("last Claude Code update: failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Status_says_nothing_about_a_streak_before_the_back_off(int failures) =>
        Assert.DoesNotContain(StatusWith(failures), l => l.Contains("in a row", StringComparison.Ordinal));

    [Fact]
    public void Status_follows_the_configured_back_off_threshold()
    {
        using var two = new ClaudeKit(c => c with { ClaudeFailuresBeforeBackoff = 2 });
        two.AllowAll();
        ClaudeUpdateState.Change(two.Config, _ => new ClaudeUpdateState(Result: ClaudeCodes.Failed, Detail: "x", Failures: 2));
        Assert.Contains(ClaudeUpdateCommand.Describe(two.Config, two.Store, two.Clock), l => l.Contains("failed 2 times in a row", StringComparison.Ordinal));
    }
}
