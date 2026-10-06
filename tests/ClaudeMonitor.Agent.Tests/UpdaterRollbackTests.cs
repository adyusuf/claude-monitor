using System.IO.Compression;
using System.Net;
using System.Text;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Updater.ApplyAsync after the swap: a new daemon that is not healthy in time is rolled back; one that cannot be stopped keeps the old version.</summary>
public sealed class UpdaterRollbackTests : IDisposable
{
    private UpdateKit kit = new();

    public void Dispose() => kit.Dispose();

    private Task<UpdateResult> Refused(UpdateOffer offer, string code) => kit.ApplyRefusedAsync(offer, code);

    // ---- the daemon after the swap ---------------------------------------------------------------------------

    [Fact]
    public async Task A_new_daemon_that_never_answers_is_rolled_back_to_the_old_binary()
    {
        kit.Dispose();
        kit = new UpdateKit(healthy: false);
        kit.InstallOld();
        var offer = kit.PublishGood();

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.RolledBack, result.Code);
        Assert.False(result.Succeeded);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
        var bin = "start:" + kit.Config.BinaryPath;
        Assert.Equal(["stop", bin, "stop", bin], kit.Daemon.Calls);
        Assert.True(((VirtualClock)kit.Clock).Elapsed >= kit.Config.UpdateHealthWait, "it waited the whole health window");
        var state = kit.State;
        Assert.Equal(UpdateCodes.RolledBack, state.Result);
        Assert.Equal(offer.Version, state.BlockedVersion);
        Assert.Null(state.Phase);
        Assert.Null(state.InstalledAt);
        Assert.False(File.Exists(kit.Staged));
        Assert.False(Directory.Exists(kit.Config.UpdateDir));
    }

    [Fact]
    public async Task A_daemon_that_turns_healthy_only_after_the_window_is_still_rolled_back()
    {
        kit.Dispose();
        kit = new UpdateKit(healthy: false);
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Daemon.OnStop = d =>
        {
            if (d.Calls.Count(c => c == "stop") == 2) d.WriteHealthMarkers(); // it answers, but only once the wait is over
        };

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.RolledBack, result.Code);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
        Assert.Equal(offer.Version, kit.State.BlockedVersion);
    }

    [Fact]
    public async Task Health_markers_from_before_the_swap_do_not_count()
    {
        kit.Dispose();
        kit = new UpdateKit(healthy: false);
        kit.InstallOld();
        var offer = kit.PublishGood();
        using (var store = new LocalStore(kit.Config.DatabasePath))
        {
            store.Set(UpdateHealth.VersionKey, offer.Version);
            store.Set(Relay.LastContactKey, kit.Clock.GetUtcNow().AddHours(-1).ToString("O")); // the old daemon's last answer
        }

        Assert.Equal(UpdateCodes.RolledBack, (await kit.Updater.ApplyAsync(offer, CancellationToken.None)).Code);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
    }

    [Fact]
    public async Task A_daemon_that_answers_but_is_not_the_new_version_is_rolled_back()
    {
        kit.Dispose();
        kit = new UpdateKit(healthy: false);
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Daemon.Version = AgentConfig.Version; // the old build came up again
        kit.Daemon.OnStop = d => { };
        using (var store = new LocalStore(kit.Config.DatabasePath))
        {
            store.Set(UpdateHealth.VersionKey, AgentConfig.Version);
            store.Set(Relay.LastContactKey, kit.Clock.GetUtcNow().AddMinutes(5).ToString("O"));
        }

        Assert.Equal(UpdateCodes.RolledBack, (await kit.Updater.ApplyAsync(offer, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task A_daemon_that_does_not_stop_in_time_keeps_the_old_version_and_nothing_is_rolled_back()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Daemon.StopResult = false;

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Contains("did not stop", result.Message, StringComparison.Ordinal);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath)); // the file is in place for its next start
        Assert.Equal(["stop"], kit.Daemon.Calls); // never started a second daemon
        Assert.Equal(UpdateCodes.Installed, kit.State.Result);
        Assert.Null(kit.State.BlockedVersion);
    }

    [Fact]
    public async Task With_no_daemon_and_no_connection_the_binary_is_replaced_and_nothing_is_started()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Daemon.Running = false;

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath));
        Assert.Empty(kit.Daemon.Calls);
        Assert.NotNull(kit.State.InstalledAt);
    }

    [Fact]
    public async Task A_connected_agent_without_a_running_daemon_gets_one_started_and_watched()
    {
        kit.InstallOld();
        kit.Connect();
        var offer = kit.PublishGood();
        kit.Daemon.Running = false;

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Equal(["start:" + kit.Config.BinaryPath], kit.Daemon.Calls); // no stop: there was nothing to stop
    }

    [Fact]
    public async Task A_connected_agent_whose_new_daemon_is_unhealthy_is_rolled_back_after_the_start()
    {
        kit.Dispose();
        kit = new UpdateKit(healthy: false);
        kit.InstallOld();
        kit.Connect();
        var offer = kit.PublishGood();
        kit.Daemon.Running = false;

        Assert.Equal(UpdateCodes.RolledBack, (await kit.Updater.ApplyAsync(offer, CancellationToken.None)).Code);
        var bin = "start:" + kit.Config.BinaryPath;
        Assert.Equal([bin, "stop", bin], kit.Daemon.Calls);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
    }
}
