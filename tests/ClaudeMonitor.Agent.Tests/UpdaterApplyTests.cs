using System.IO.Compression;
using System.Net;
using System.Text;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Updater.ApplyAsync: download, verify, replace; and every refusal leaves the installation exactly as it was.</summary>
public sealed class UpdaterApplyTests : IDisposable
{
    private UpdateKit kit = new();

    public void Dispose() => kit.Dispose();

    private static void Serve(UpdateKit k, UpdateOffer offer, byte[] zip) => k.Host.Files[new Uri(offer.Url).AbsolutePath] = zip;

    /// <summary>Applies the offer and checks the refusal: the code, the recorded state, the disk untouched, the daemon left alone.</summary>
    private async Task<UpdateResult> Refused(UpdateOffer offer, string code)
    {
        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);
        Assert.Equal(code, result.Code);
        Assert.False(result.Succeeded);
        kit.AssertUntouched();
        Assert.Equal(code, kit.State.Result);
        Assert.Null(kit.State.Phase);
        Assert.Null(kit.State.InstalledAt);
        Assert.Empty(kit.Daemon.Calls);
        Assert.True(kit.Daemon.Running, "nobody stopped the daemon");
        return result;
    }

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
            Assert.Equal("codesign", calls[0].File);
            Assert.Equal(["--verify", "--strict", kit.Staged], calls[0].Args);
        }
        else
        {
            Assert.DoesNotContain(calls, c => c.File == "codesign");
        }
    }

    // ---- refusals that leave the disk untouched ---------------------------------------------------------------

    [Fact]
    public async Task A_download_whose_hash_is_not_the_signed_one_is_refused()
    {
        kit.InstallOld();
        var offer = kit.Offer(kit.Zip(Encoding.ASCII.GetBytes("WHAT WAS SIGNED")));
        Serve(kit, offer, kit.Zip(Encoding.ASCII.GetBytes("SOMETHING ELSE")));
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.HashMismatch);
        Assert.NotNull(kit.State.AttemptedAt);
        Assert.Single(kit.Host.Downloads); // it was downloaded, then refused
    }

    [Theory]
    [InlineData("https://evil.invalid/downloads/a.zip")]
    [InlineData("https://monitor.invalid:8443/downloads/a.zip")]
    [InlineData("http://monitor.invalid/downloads/a.zip")]
    [InlineData("https://monitor.invalid/other/a.zip")]
    [InlineData("https://monitor.invalid/downloadsx/a.zip")]
    [InlineData("https://monitor.invalid/")]
    [InlineData("/downloads/a.zip")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_download_address_off_this_servers_downloads_is_refused_before_anything_is_fetched(string url)
    {
        kit.InstallOld();
        var zip = kit.Zip();
        var offer = kit.Offer(zip) with { Url = url };
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.BadUrl);
        Assert.Empty(kit.Host.Downloads);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_redirected_download_is_refused()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.RedirectedTo = new Uri("https://evil.invalid/downloads/elsewhere.zip");
        await Refused(offer, UpdateCodes.BadUrl);
        Assert.Empty(kit.Runner.Calls);
    }

    private static byte[] EmptyZip()
    {
        using var buffer = new MemoryStream();
        using (new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task An_archive_with_two_files_is_refused()
    {
        kit.InstallOld();
        var zip = kit.Zip(null, kit.BinaryName, "extra.txt");
        var offer = kit.Offer(zip);
        Serve(kit, offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.BadArchive);
        Assert.Empty(kit.Runner.Calls);
    }

    [Theory]
    [InlineData("other-name")]
    [InlineData("../cm-agent")]
    [InlineData("../../cm-agent")]
    [InlineData("sub/cm-agent")]
    [InlineData("/cm-agent")]
    public async Task An_archive_whose_file_is_not_named_like_the_binary_is_refused(string entry)
    {
        kit.InstallOld();
        var zip = kit.Zip(null, entry.Replace("cm-agent", kit.BinaryName, StringComparison.Ordinal));
        var offer = kit.Offer(zip);
        Serve(kit, offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.BadArchive);
        Assert.False(File.Exists(Path.Combine(kit.Dir, "cm-agent")), "nothing was written outside the home");
    }

    [Fact]
    public async Task An_empty_archive_is_refused()
    {
        kit.InstallOld();
        var zip = EmptyZip();
        var offer = kit.Offer(zip);
        Serve(kit, offer, zip);
        await Refused(offer, UpdateCodes.BadArchive);
    }

    [Fact]
    public async Task A_download_larger_than_allowed_is_refused_by_its_announced_size()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdateDownloadMax = 10 });
        kit.InstallOld();
        var offer = kit.PublishGood();
        await Refused(offer, UpdateCodes.TooLarge);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_download_that_hides_its_size_is_refused_by_counting_the_bytes()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdateDownloadMax = 10 });
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.UnknownLength = true;
        await Refused(offer, UpdateCodes.TooLarge);
    }

    [Fact]
    public async Task A_binary_larger_than_allowed_inside_the_archive_is_refused()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdateBinaryMax = 50 });
        kit.InstallOld();
        var zip = kit.Zip(new byte[500]);
        var offer = kit.Offer(zip);
        Serve(kit, offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        Assert.True(zip.Length < kit.Config.UpdateDownloadMax);
        await Refused(offer, UpdateCodes.TooLarge);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_binary_that_reports_another_version_is_refused()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.VersionOutput = UpdateKit.Newest;
        await Refused(offer, UpdateCodes.BadBinary);
    }

    [Fact]
    public async Task A_binary_that_does_not_run_is_refused_even_if_it_printed_the_right_version()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.VersionExit = 1;
        await Refused(offer, UpdateCodes.BadBinary);
    }

    [Fact]
    public async Task A_binary_that_prints_nothing_is_refused()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.VersionOutput = "";
        await Refused(offer, UpdateCodes.BadBinary);
    }

    [Fact]
    public async Task On_macos_a_binary_without_a_valid_code_signature_is_refused_without_being_run()
    {
        if (!OperatingSystem.IsMacOS()) return;
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.CodesignExit = 1;
        await Refused(offer, UpdateCodes.BadBinary);
        var call = Assert.Single(kit.Runner.Calls);
        Assert.Equal("codesign", call.File);
        Assert.Equal(["--verify", "--strict", kit.Staged], call.Args);
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

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_download_that_is_not_200_fails(HttpStatusCode status)
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.DownloadStatus = status;
        var result = await Refused(offer, UpdateCodes.Failed);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
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
