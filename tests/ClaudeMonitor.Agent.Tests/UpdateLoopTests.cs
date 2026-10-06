using System.Net;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The daemon's update chore: with the effective mode off it does nothing at all, not even a network call.</summary>
public sealed class UpdateLoopTests : IDisposable
{
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly UpdateKit kit;
    private readonly LocalStore store;
    private readonly UpdateLoop loop;
    private int installs;

    public UpdateLoopTests()
    {
        kit = new UpdateKit(clock: clock);
        store = new LocalStore(kit.Config.DatabasePath);
        loop = new UpdateLoop(kit.Config, store, kit.Updater, () =>
        {
            installs++;
            return true;
        }, clock);
        kit.Publish(kit.Offer(kit.Zip()));
    }

    public void Dispose()
    {
        store.Dispose();
        kit.Dispose();
    }

    private int Asked => kit.Api.Count("GET /api/agent/latest");

    private void Machine(string mode) => SavedSettings.SaveAutoUpdate(kit.Config, mode);

    private void Workspace(string? mode)
    {
        if (mode is not null) store.Set(UpdatePolicy.WorkspaceKey, mode);
    }

    [Fact]
    public async Task By_default_nothing_happens_not_even_a_call()
    {
        Workspace(UpdateModes.On); // the workspace allows it, the machine never said yes
        await loop.RunAsync(CancellationToken.None);
        Assert.Empty(kit.Api.Seen);
        Assert.Equal(0, installs);
        Assert.False(File.Exists(kit.Config.UpdateStatePath));
    }

    [Theory]
    [InlineData("off", "on", 0, false)]
    [InlineData("on", "off", 0, false)]
    [InlineData("on", null, 0, false)] // the workspace's cap is unknown: off
    [InlineData("on", "bogus", 0, false)]
    [InlineData("off", "off", 0, false)]
    [InlineData("check", "on", 1, false)]
    [InlineData("on", "check", 1, false)]
    [InlineData("check", "check", 1, false)]
    [InlineData("on", "on", 1, true)]
    public async Task The_lower_of_the_machines_and_the_workspaces_mode_decides(string machine, string? workspace, int asks, bool installs_)
    {
        Machine(machine);
        Workspace(workspace);
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal(asks, Asked);
        Assert.Equal(installs_ ? 1 : 0, installs);
        Assert.Equal(asks, kit.Api.Seen.Count); // nothing else was said to the server
        Assert.Equal(asks == 1, File.Exists(kit.Config.UpdateStatePath));
        if (asks == 1) Assert.Equal(UpdateCodes.Available, kit.State.Result);
    }

    [Fact]
    public async Task A_second_round_within_the_check_interval_does_not_ask_again_and_one_after_it_does()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((1, 1), (Asked, installs));

        clock.Advance(kit.Config.UpdateCheckEvery - TimeSpan.FromMinutes(1));
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((1, 1), (Asked, installs));

        clock.Advance(TimeSpan.FromMinutes(2));
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task A_build_that_was_rolled_back_is_looked_at_but_not_installed_again()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        var offer = kit.Publish(kit.Offer(kit.Zip()));
        UpdateState.Change(kit.Config, s => s with { BlockedVersion = offer.Version });
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((1, 0), (Asked, installs));
        Assert.Equal(offer.Version, kit.State.Available);
    }

    [Fact]
    public async Task A_different_build_than_the_rolled_back_one_is_installed()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        UpdateState.Change(kit.Config, s => s with { BlockedVersion = UpdateKit.Newer });
        kit.Publish(kit.Offer(kit.Zip(), UpdateKit.Newest));
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal(1, installs);
    }

    [Theory]
    [InlineData(-10, 0)] // ten minutes ago: too soon after a failed attempt
    [InlineData(-59, 0)]
    [InlineData(-61, 1)] // more than UpdateRetryAfter ago
    [InlineData(-600, 1)]
    public async Task A_recent_attempt_is_not_repeated_at_once(int minutesAgo, int expectedInstalls)
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        UpdateState.Change(kit.Config, s => s with { AttemptedAt = clock.GetUtcNow().AddMinutes(minutesAgo).ToString("O") });
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal(expectedInstalls, installs);
        Assert.Equal(1, Asked);
    }

    [Fact]
    public async Task An_update_in_progress_is_not_started_again()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        UpdateState.Change(kit.Config, s => s with { Phase = UpdateState.PendingHealth });
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal(0, installs);
    }

    [Fact]
    public async Task A_refused_offer_is_never_started()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        kit.Publish(kit.Offer(kit.Zip()) with { Signature = "AAAA" });
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((1, 0), (Asked, installs));
        Assert.Equal(UpdateCodes.BadSignature, kit.State.Result);
    }

    [Fact]
    public async Task An_unreachable_server_does_not_throw_out_of_the_round()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        kit.Api.On("GET /api/agent/latest*", _ => throw new HttpRequestException("down"));
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((1, 0), (Asked, installs));
        Assert.Equal(UpdateCodes.Unreachable, kit.State.Result);
    }

    [Fact]
    public async Task The_saved_setting_and_the_workspaces_cap_are_read_afresh_on_every_round()
    {
        await loop.RunAsync(CancellationToken.None); // off
        Assert.Equal(0, Asked);

        Machine(UpdateModes.Check); // the person changes it while the daemon runs
        await loop.RunAsync(CancellationToken.None); // the workspace is still unknown
        Assert.Equal(0, Asked);

        Workspace(UpdateModes.On); // the daemon's settings poll brings the workspace's cap
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((1, 0), (Asked, installs)); // check only

        Machine(UpdateModes.On);
        clock.Advance(kit.Config.UpdateCheckEvery + TimeSpan.FromMinutes(1));
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((2, 1), (Asked, installs));

        Machine(UpdateModes.Off);
        clock.Advance(kit.Config.UpdateCheckEvery + TimeSpan.FromMinutes(1));
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal((2, 1), (Asked, installs)); // off again: silent
    }

    [Fact]
    public async Task CM_AUTO_UPDATE_from_the_environment_wins_over_the_saved_setting()
    {
        using var forced = new UpdateKit(c => c with { AutoUpdate = UpdateModes.Off, AutoUpdateFromEnvironment = true }, clock);
        using var forcedStore = new LocalStore(forced.Config.DatabasePath);
        var calls = 0;
        var forcedLoop = new UpdateLoop(forced.Config, forcedStore, forced.Updater, () => ++calls > 0, clock);
        forced.Publish(forced.Offer(forced.Zip()));
        SavedSettings.SaveAutoUpdate(forced.Config, UpdateModes.On);
        forcedStore.Set(UpdatePolicy.WorkspaceKey, UpdateModes.On);
        await forcedLoop.RunAsync(CancellationToken.None);
        Assert.Empty(forced.Api.Seen);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_server_error_is_recorded_and_does_not_throw_out_of_the_round()
    {
        Machine(UpdateModes.On);
        Workspace(UpdateModes.On);
        kit.Api.On("GET /api/agent/latest*", HttpStatusCode.InternalServerError, "{}");
        await loop.RunAsync(CancellationToken.None);
        Assert.Equal(0, installs);
        Assert.Equal(UpdateCodes.Unreachable, kit.State.Result);
    }
}
