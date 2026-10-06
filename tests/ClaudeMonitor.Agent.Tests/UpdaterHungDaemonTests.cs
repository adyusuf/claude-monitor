using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// A new daemon that is not healthy may also be hung, still holding the daemon's lock when the rollback asks it to stop. The
/// rollback then ends it (Kill), puts the old bytes back and starts the old daemon; if it cannot end it, the old bytes are
/// still put back but no second daemon is started and the result says so.
/// </summary>
public sealed class UpdaterHungDaemonTests : IDisposable
{
    private readonly UpdateKit kit = new(healthy: false);

    public void Dispose() => kit.Dispose();

    private string Bin => "start:" + kit.Config.BinaryPath;

    private UpdateOffer Prepare()
    {
        kit.InstallOld();
        return kit.PublishGood();
    }

    [Fact]
    public async Task A_hung_new_daemon_is_killed_the_old_bytes_come_back_and_the_old_daemon_is_started()
    {
        var offer = Prepare();
        kit.Daemon.ScriptStops(true, false); // the old daemon stops; the new, hung one does not
        kit.Daemon.KillResult = true;

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.RolledBack, result.Code);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
        Assert.Equal(1, kit.Daemon.Kills);
        Assert.Equal(2, kit.Daemon.Starts);
        Assert.Equal(["stop", Bin, "stop", "kill", Bin], kit.Daemon.Calls); // the kill comes after the stop failed, the start after the kill
        Assert.True(kit.Daemon.Running);
        Assert.Equal(UpdateCodes.RolledBack, kit.State.Result);
        Assert.Equal(offer.Version, kit.State.BlockedVersion);
        Assert.Null(kit.State.Phase);
        Assert.Null(kit.State.InstalledAt);
    }

    [Fact]
    public async Task When_the_hung_daemon_cannot_be_killed_the_old_bytes_are_back_but_no_daemon_is_started_and_the_result_says_so()
    {
        var offer = Prepare();
        kit.Daemon.ScriptStops(true, false);
        kit.Daemon.KillResult = false; // the lock is still held

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.RollbackStuck, result.Code);
        Assert.False(result.Succeeded);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath)); // every hook already runs the old binary
        if (!OperatingSystem.IsWindows()) Assert.False(File.Exists(kit.Previous), "the previous version was moved back, not copied");
        Assert.Equal(1, kit.Daemon.Kills);
        Assert.Equal(1, kit.Daemon.Starts); // only the update's own start; none after the failed kill
        Assert.Equal(["stop", Bin, "stop", "kill"], kit.Daemon.Calls);
        var state = kit.State;
        Assert.Equal(UpdateCodes.RollbackStuck, state.Result);
        Assert.Equal(offer.Version, state.BlockedVersion); // never retried by itself
        Assert.Null(state.Phase);
        Assert.Null(state.InstalledAt);
        Assert.Contains($"update {UpdateCodes.RollbackStuck}", await File.ReadAllTextAsync(kit.Config.LogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(kit.Staged));
    }

    [Fact]
    public async Task A_rollback_whose_stop_works_never_kills()
    {
        var offer = Prepare();

        Assert.Equal(UpdateCodes.RolledBack, (await kit.Updater.ApplyAsync(offer, CancellationToken.None)).Code);

        Assert.Equal(0, kit.Daemon.Kills);
        Assert.Equal(["stop", Bin, "stop", Bin], kit.Daemon.Calls);
    }

    [Fact]
    public async Task An_old_daemon_that_does_not_stop_is_never_killed_by_the_update_itself()
    {
        var offer = Prepare();
        kit.Daemon.StopResult = false;

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Equal(0, kit.Daemon.Kills);
        Assert.Equal(["stop"], kit.Daemon.Calls);
    }
}
