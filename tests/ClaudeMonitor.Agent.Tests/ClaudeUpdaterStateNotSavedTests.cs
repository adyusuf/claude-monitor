using System.Text.RegularExpressions;
using ClaudeMonitor.Agent.ClaudeUpdate;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A state file that cannot be written fails closed: the countdown could not be cancelled, so nothing runs, and this process does not retry in a loop.</summary>
public sealed class ClaudeUpdaterStateNotSavedTests : IDisposable
{
    private readonly ClaudeKit kit = new();

    public ClaudeUpdaterStateNotSavedTests()
    {
        kit.AllowAll();
        kit.Idle();
    }

    public void Dispose() => kit.Dispose();

    /// <summary>A folder where the file should be: nothing can be written to that name, whoever runs the test.</summary>
    private void BlockTheStateFile() => Directory.CreateDirectory(kit.Config.ClaudeUpdateStatePath);

    [Fact]
    public async Task A_countdown_that_cannot_be_saved_runs_nothing_and_tells_nobody()
    {
        BlockTheStateFile();
        var outcome = await kit.RunAsync();

        Assert.Equal(ClaudeCodes.NotSaved, outcome.Code);
        Assert.Contains("the countdown could not be saved, so it could not be cancelled: nothing was run", outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened(); // no notice, no `--version`, no `claude update`
        Assert.Empty(Directory.GetFiles(kit.Config.Home, "*.tmp")); // the half-written file did not stay behind
        Assert.Matches(@"claude-update-state\.json could not be saved \((IOException|UnauthorizedAccessException)\)", kit.LogText);
        Assert.Contains("claude update state-not-saved:", kit.LogText, StringComparison.Ordinal);
        Assert.DoesNotContain(kit.Config.ClaudeUpdateStatePath, kit.LogText, StringComparison.Ordinal); // the error's message (it names the path) is not logged
    }

    [Fact]
    public async Task A_second_attempt_in_this_process_is_held_until_the_usual_interval_has_passed()
    {
        BlockTheStateFile();
        Assert.Equal(ClaudeCodes.NotSaved, (await kit.RunAsync()).Code);
        Assert.Contains("no attempt starts before", kit.LogText, StringComparison.Ordinal);

        // the file cannot say "not before ...", so the process does: even after the retry time it stays held
        kit.Clock.Advance(kit.Config.UpdateRetryAfter + TimeSpan.FromMinutes(1));
        var held = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.NotDue, held.Code);
        Assert.Contains("could not be saved", held.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened();

        // ...and is let go after the usual interval, trying once more
        kit.Clock.Advance(kit.Config.ClaudeUpdateEvery);
        Assert.Equal(ClaudeCodes.NotSaved, (await kit.RunAsync()).Code);
    }

    [Fact]
    public async Task A_result_that_cannot_be_saved_after_an_update_holds_the_next_attempt_so_it_is_not_run_again_at_once()
    {
        kit.Runner.Versions("2.1.285 (Claude Code)", "2.1.291 (Claude Code)");
        kit.Runner.OnUpdate = () =>
        {
            File.Delete(kit.Config.ClaudeUpdateStatePath); // the countdown's record was saved; now the file becomes unwritable
            BlockTheStateFile();
        };

        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Updated, outcome.Code); // the update itself happened and is reported
        Assert.Contains("the result could not be saved; no attempt starts before", kit.LogText, StringComparison.Ordinal);

        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.NotDue, again.Code);
        Assert.Contains("(the last result could not be saved)", again.Detail, StringComparison.Ordinal);
        Assert.Equal(1, kit.Runner.Updates);
        Assert.Single(kit.Notifier.Messages);
    }

    [Fact]
    public async Task A_result_that_is_saved_holds_nothing_back_in_the_process()
    {
        Assert.Equal(ClaudeCodes.Unchanged, (await kit.RunAsync()).Code);
        Assert.DoesNotContain("could not be saved", kit.LogText, StringComparison.Ordinal);
        // only NextAt gates it: clearing it lets the very next call run again at once
        ClaudeUpdateState.Change(kit.Config, s => s with { NextAt = null });
        Assert.Equal(ClaudeCodes.Unchanged, (await kit.RunAsync()).Code);
        Assert.Equal(2, kit.Runner.Updates);
    }

    // ---- ClaudeUpdateState.Change itself -----------------------------------------------------------------------

    [Fact]
    public void Change_returns_false_and_logs_only_the_error_type_when_the_file_cannot_be_written()
    {
        BlockTheStateFile();
        const string content = "DO-NOT-LOG-THIS-DETAIL";
        var saved = ClaudeUpdateState.Change(kit.Config, s => s with { Detail = content }, kit.Log);

        Assert.False(saved);
        var line = Assert.Single(kit.LogText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Matches(new Regex(@"claude update: claude-update-state\.json could not be saved \((IOException|UnauthorizedAccessException)\)$"), line);
        Assert.DoesNotContain(content, kit.LogText, StringComparison.Ordinal);
        Assert.DoesNotContain(kit.Config.Home, line.Split(" [", 2)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Change_without_a_log_still_returns_false_instead_of_throwing()
    {
        BlockTheStateFile();
        Assert.False(ClaudeUpdateState.Change(kit.Config, s => s with { Detail = "x" }));
    }

    [Fact]
    public void Change_returns_true_and_the_state_reads_back_when_it_could_be_written()
    {
        Assert.True(ClaudeUpdateState.Change(kit.Config, s => s with { Detail = "written", Failures = 2 }, kit.Log));
        Assert.Equal(("written", 2), (kit.State.Detail, kit.State.Failures));
        Assert.Equal("", kit.LogText);
    }
}
