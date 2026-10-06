using System.Text.Json;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// An update whose new version could not be read (the agent stopped) is not lost: the interrupted state remembers the version
/// before it, and the next attempt that finds a different one reports "old -> new" instead of "up to date".
/// </summary>
public sealed class ClaudeUpdaterPendingTests : IDisposable
{
    private const string Old = "2.1.285";
    private const string New = "2.2.0";

    private readonly ClaudeKit kit = new();
    private readonly ScriptedClaudeRunner runner = new();
    private readonly ClaudeUpdater updater;

    public ClaudeUpdaterPendingTests()
    {
        updater = new ClaudeUpdater(kit.Config, kit.Store, runner, kit.Notifier, kit.Log, kit.Clock, pid => kit.Alive(pid));
        kit.AllowAll();
        kit.Idle();
    }

    public void Dispose() => kit.Dispose();

    private async Task CutShortUpdateAsync()
    {
        using var stop = new CancellationTokenSource();
        runner.OnUpdate = _ =>
        {
            stop.Cancel(); // the update finished; the stop lands before the new version is read
            runner.Version = $"{New} (Claude Code)"; // what the update installed
            return new(ProcessEnd.Exited, 0, "");
        };
        var outcome = await updater.RunAsync(stop.Token);
        Assert.Equal(ClaudeCodes.Interrupted, outcome.Code);
        runner.OnUpdate = _ => new(ProcessEnd.Exited, 0, "");
        kit.Clock.Advance(kit.Config.UpdateRetryAfter + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_cut_short_version_read_remembers_the_version_before_and_the_next_attempt_reports_the_update()
    {
        await CutShortUpdateAsync();
        Assert.Equal(Old, kit.State.PendingFrom);

        var next = await updater.RunAsync(CancellationToken.None);

        Assert.Equal(ClaudeCodes.Updated, next.Code);
        Assert.Contains($"Claude Code {Old} -> {New}", next.Detail, StringComparison.Ordinal);
        Assert.Equal(Old, kit.State.VersionBefore);
        Assert.Equal(New, kit.State.VersionAfter);
        Assert.Null(kit.State.PendingFrom); // told once
    }

    [Fact]
    public async Task Once_told_the_following_attempt_is_plainly_up_to_date()
    {
        await CutShortUpdateAsync();
        await updater.RunAsync(CancellationToken.None);
        kit.Clock.Advance(kit.Config.ClaudeUpdateEvery + TimeSpan.FromMinutes(1));

        var again = await updater.RunAsync(CancellationToken.None);

        Assert.Equal(ClaudeCodes.Unchanged, again.Code);
        Assert.Contains($"up to date ({New})", again.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remembered_version_equal_to_the_one_found_is_just_up_to_date_and_is_cleared()
    {
        await CutShortUpdateAsync();
        runner.Version = ScriptedClaudeRunner.VersionText; // the update had not changed anything after all

        var next = await updater.RunAsync(CancellationToken.None);

        Assert.Equal(ClaudeCodes.Unchanged, next.Code);
        Assert.Null(kit.State.PendingFrom);
    }

    [Fact]
    public async Task A_state_file_without_the_remembered_version_still_loads()
    {
        await File.WriteAllTextAsync(kit.Config.ClaudeUpdateStatePath, JsonSerializer.Serialize(new { result = "interrupted", versionBefore = Old, failures = 2 }));
        var state = kit.State;
        Assert.Equal((ClaudeCodes.Interrupted, Old, 2), (state.Result, state.VersionBefore, state.Failures));
        Assert.Null(state.PendingFrom);
    }
}
