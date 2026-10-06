using System.IO.Compression;
using System.Net;
using System.Text;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Updater.ApplyAsync: a verified build replaces the binary and the daemon restarts; a second update at once, or none installed, is refused.</summary>
public sealed class UpdaterApplyTests : IDisposable
{
    private UpdateKit kit = new();

    public void Dispose() => kit.Dispose();

    private Task<UpdateResult> Refused(UpdateOffer offer, string code) => kit.ApplyRefusedAsync(offer, code);

    // ---- the happy path ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_build_replaces_the_binary_keeps_the_old_one_and_restarts_the_daemon()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        Assert.Equal(UpdateCodes.Available, (await kit.Updater.CheckAsync(CancellationToken.None)).Code);
        Assert.Equal(offer.Version, kit.State.Available);

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.True(result.Succeeded);
        Assert.Equal((AgentConfig.Version, offer.Version), (result.From, result.To));
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath));
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Previous));
        Assert.Equal(["stop", "start:" + kit.Config.BinaryPath], kit.Daemon.Calls);
        Assert.False(File.Exists(kit.Staged));
        Assert.False(Directory.Exists(kit.Config.UpdateDir));
        var state = kit.State;
        Assert.Equal(UpdateCodes.Installed, state.Result);
        Assert.NotNull(state.InstalledAt);
        Assert.NotNull(state.AttemptedAt);
        Assert.Null(state.Phase);
        Assert.Null(state.Available); // nothing left to install
        Assert.Null(state.BlockedVersion);
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(kit.Config.BinaryPath).HasFlag(UnixFileMode.UserExecute));
        using var free = DaemonHost.TryLock(kit.Config.UpdateLockPath);
        Assert.NotNull(free); // the update lock is released
    }

    [Fact]
    public async Task The_downloaded_binary_is_asked_for_its_version_before_it_is_trusted()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        await kit.Updater.ApplyAsync(offer, CancellationToken.None);
        var calls = kit.Runner.Calls.ToArray();
        Assert.Contains(calls, c => c.File == kit.Staged && c.Args.SequenceEqual(["version"]));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(FakeRunner.Codesign, calls[0].File);
            Assert.Equal(["--verify", "--strict", kit.Staged], calls[0].Args);
        }
        else
        {
            Assert.DoesNotContain(calls, c => c.File == FakeRunner.Codesign);
        }
    }

    [Fact]
    public async Task A_second_update_at_the_same_time_is_refused_as_busy_and_the_first_is_not_disturbed()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        using (var held = DaemonHost.TryLock(kit.Config.UpdateLockPath))
        {
            Assert.NotNull(held);
            await Refused(offer, UpdateCodes.Busy);
            Assert.Empty(kit.Host.Downloads);
        }

        Assert.Equal(UpdateCodes.Installed, (await kit.Updater.ApplyAsync(offer, CancellationToken.None)).Code); // once it is free, it goes
    }

    [Fact]
    public async Task Without_an_installed_binary_there_is_nothing_to_replace()
    {
        var offer = kit.PublishGood();
        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);
        Assert.Equal(UpdateCodes.NotInstalled, result.Code);
        Assert.False(File.Exists(kit.Config.BinaryPath));
        Assert.False(File.Exists(kit.Previous));
        Assert.Empty(kit.Host.Downloads);
        Assert.Empty(kit.Daemon.Calls);
        Assert.Equal(UpdateCodes.NotInstalled, kit.State.Result);
    }

    [Fact]
    public async Task A_failed_attempt_is_remembered_so_the_daemon_does_not_retry_at_once()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.DownloadStatus = HttpStatusCode.BadGateway;
        await kit.Updater.ApplyAsync(offer, CancellationToken.None);
        Assert.True(DateTimeOffset.TryParse(kit.State.AttemptedAt, out var at));
        Assert.True(kit.Clock.GetUtcNow() - at < TimeSpan.FromMinutes(1));
    }
}
