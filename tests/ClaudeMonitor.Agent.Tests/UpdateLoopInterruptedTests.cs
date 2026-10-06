using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// "pending-health" is true only while an updater holds update.lock. Found with nobody holding it (the updater was killed,
/// the machine lost power) it is settled from what is known; otherwise it would block every automatic update for ever.
/// </summary>
public sealed class UpdateLoopInterruptedTests : IDisposable
{
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly UpdateKit kit;
    private readonly LocalStore store;
    private readonly UpdateLoop loop;
    private int installs;

    public UpdateLoopInterruptedTests()
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

    private void Pending(string to) =>
        UpdateState.Change(kit.Config, s => s with { Phase = UpdateState.PendingHealth, From = "0.0.1", To = to, Result = UpdateCodes.Available });

    private void AllowInstalls()
    {
        SavedSettings.SaveAutoUpdate(kit.Config, UpdateModes.On);
        TestWorkspace.Set(kit.Config, store, UpdatePolicy.WorkspaceKey, UpdateModes.On);
    }

    [Fact]
    public async Task A_pending_update_whose_target_is_the_running_version_is_settled_as_installed_with_no_call_in_the_default_off_mode()
    {
        Pending(AgentConfig.Version);

        await loop.RunAsync(CancellationToken.None);

        var state = kit.State;
        Assert.Equal(UpdateCodes.Installed, state.Result);
        Assert.Null(state.Phase);
        Assert.Equal(clock.GetUtcNow(), DateTimeOffset.Parse(state.InstalledAt!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(AgentConfig.Version, state.Detail, StringComparison.Ordinal);
        Assert.Empty(kit.Api.Seen); // off: not even one HTTP call
        Assert.Equal(0, installs);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("on")]
    public async Task The_settlement_does_not_depend_on_the_machines_mode_when_the_workspace_has_not_allowed_anything(string machine)
    {
        SavedSettings.SaveAutoUpdate(kit.Config, machine); // the workspace's cap is unknown, so the effective mode is off
        Pending(AgentConfig.Version);

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, kit.State.Result);
        Assert.Null(kit.State.Phase);
        Assert.Empty(kit.Api.Seen);
    }

    [Fact]
    public async Task A_pending_update_whose_target_is_not_the_running_version_is_settled_as_interrupted()
    {
        Pending(UpdateKit.Newest);

        await loop.RunAsync(CancellationToken.None);

        var state = kit.State;
        Assert.Equal(UpdateCodes.Interrupted, state.Result);
        Assert.Null(state.Phase);
        Assert.Null(state.InstalledAt); // nothing was installed
        Assert.Contains(UpdateKit.Newest, state.Detail, StringComparison.Ordinal);
        Assert.Empty(kit.Api.Seen);
    }

    [Fact]
    public async Task While_an_updater_holds_the_lock_the_state_is_left_untouched()
    {
        Pending(AgentConfig.Version);
        var before = kit.State;

        using (var updater = DaemonHost.TryLock(kit.Config.UpdateLockPath))
        {
            Assert.NotNull(updater);
            await loop.RunAsync(CancellationToken.None); // off: nothing but the settling could write
            Assert.Equal(before, kit.State);
            Assert.Empty(kit.Api.Seen);
        }

        // once the holder is gone the very next round settles it
        await loop.RunAsync(CancellationToken.None);
        Assert.Null(kit.State.Phase);
        Assert.Equal(UpdateCodes.Installed, kit.State.Result);
        Assert.NotNull(kit.State.InstalledAt);
    }

    [Fact]
    public async Task While_an_updater_holds_the_lock_no_install_is_started_and_the_phase_stays()
    {
        AllowInstalls();
        Pending(AgentConfig.Version);

        using var updater = DaemonHost.TryLock(kit.Config.UpdateLockPath);
        Assert.NotNull(updater);
        await loop.RunAsync(CancellationToken.None); // it may still look at the server

        Assert.Equal(0, installs);
        var state = kit.State;
        Assert.Equal(UpdateState.PendingHealth, state.Phase);
        Assert.Null(state.InstalledAt); // not settled
        Assert.Equal(AgentConfig.Version, state.To);
    }

    [Fact]
    public async Task After_an_interrupted_update_is_settled_the_next_install_can_start()
    {
        AllowInstalls();
        Pending(UpdateKit.Newest); // interrupted: not the running version

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(1, installs);
        Assert.Null(kit.State.Phase);
        Assert.Null(kit.State.BlockedVersion); // an interruption is not a verdict against the build
        Assert.Equal(1, kit.Api.Count("GET /api/agent/latest"));
    }

    [Fact]
    public async Task After_an_arrived_update_is_settled_a_still_newer_build_can_start()
    {
        AllowInstalls();
        Pending(AgentConfig.Version);
        kit.Publish(kit.Offer(kit.Zip(), UpdateKit.Newest));

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(1, installs);
        Assert.NotNull(kit.State.InstalledAt);
    }

    [Fact]
    public async Task Without_a_pending_phase_nothing_is_touched()
    {
        UpdateState.Change(kit.Config, s => s with { Result = UpdateCodes.RolledBack, Detail = "d", InstalledAt = "2026-01-01T00:00:00.0000000+00:00" });
        var before = kit.State;

        await loop.RunAsync(CancellationToken.None); // off

        Assert.Equal(before, kit.State);
    }
}
