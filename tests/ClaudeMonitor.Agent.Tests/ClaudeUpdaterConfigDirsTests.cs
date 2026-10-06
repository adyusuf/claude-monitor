using ClaudeMonitor.Agent.ClaudeUpdate;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The Claude updater looks at every config folder a hook recorded lately, not only the daemon's own.</summary>
public sealed class ClaudeUpdaterConfigDirsTests : IDisposable
{
    private readonly ClaudeKit kit = new();
    private readonly SessionsDir other = new();

    public ClaudeUpdaterConfigDirsTests()
    {
        kit.AllowAll();
        kit.Idle();
        kit.Runner.Versions("2.1.285 (Claude Code)", "2.1.291 (Claude Code)");
    }

    public void Dispose()
    {
        kit.Dispose();
        other.Dispose();
    }

    private void SeenLately(TimeSpan ago = default) => kit.Store.ClaudeConfigDirSeen(other.ConfigDir, kit.Now - ago);

    [Fact]
    public async Task A_busy_session_in_a_second_recorded_folder_makes_the_updater_wait_and_run_nothing()
    {
        SeenLately();
        other.Session(200, "busy", kit.Now);

        var outcome = await kit.RunAsync();

        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("a session is busy (one of 2 Claude config folders)", outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened(ClaudeCodes.Waiting);
    }

    [Fact]
    public async Task The_same_busy_session_in_a_folder_no_hook_ever_recorded_is_not_seen_and_the_update_goes_ahead()
    {
        other.Session(200, "busy", kit.Now); // the limit of what the hooks can tell: a folder nobody has used with a hook is invisible
        Assert.Equal(ClaudeCodes.Updated, (await kit.RunAsync()).Code);
    }

    [Fact]
    public async Task A_second_folder_whose_sessions_are_all_idle_lets_the_update_go_ahead()
    {
        SeenLately();
        other.Session(200, "idle", kit.Now.AddMinutes(-30));
        Assert.Equal(ClaudeCodes.Updated, (await kit.RunAsync()).Code);
        Assert.Equal(1, kit.Runner.Updates);
    }

    [Fact]
    public async Task A_recorded_folder_that_no_longer_exists_keeps_the_updater_waiting()
    {
        SeenLately(); // nothing is created under other.Root
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("the sessions folder of Claude Code is not there", outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened();
    }

    [Fact]
    public async Task A_folder_last_recorded_longer_ago_than_the_update_interval_is_forgotten()
    {
        SeenLately(kit.Config.ClaudeUpdateEvery + TimeSpan.FromMinutes(1));
        other.Session(200, "busy", kit.Now);
        Assert.Equal(ClaudeCodes.Updated, (await kit.RunAsync()).Code);
    }

    [Fact]
    public async Task A_session_in_the_second_folder_turning_busy_during_the_countdown_stops_the_update()
    {
        SeenLately();
        other.Session(200, "idle", kit.Now.AddMinutes(-30));
        kit.Clock.OnTick = n =>
        {
            if (n == 2) other.Session(200, "busy", kit.Now);
        };

        var outcome = await kit.RunAsync();

        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("stopped: a session is busy (one of 2 Claude config folders)", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(0, kit.Runner.Updates);
        Assert.Null(kit.State.CountdownUntil);
    }
}
