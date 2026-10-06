using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// A Linux agent updates itself like a macOS one (an atomic rename over the running binary, the previous one kept), without
/// macOS's codesign. The OS is the one the build was signed for (UpdateOs), so these run the same on any host.
/// </summary>
public sealed class UpdaterLinuxTests : IDisposable
{
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private UpdateKit kit = Linux();

    public void Dispose() => kit.Dispose();

    private static UpdateKit Linux(bool healthy = true, bool service = false) =>
        new(c => c with { UpdateOs = OsKinds.Linux, UpdateArch = "x64", ServiceMode = service }, healthy: healthy);

    [Fact]
    public async Task A_linux_agent_asks_for_the_linux_build()
    {
        await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal($"?os={OsKinds.Linux}&arch=x64", Assert.Single(kit.Host.Queries));
    }

    [Fact]
    public async Task A_verified_linux_build_replaces_the_binary_executable_and_keeps_the_old_one_without_running_codesign()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath));
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Previous));
        var call = Assert.Single(kit.Runner.Calls); // only the candidate's own `version`: no macOS tool on Linux
        Assert.Equal(kit.Staged, call.File);
        Assert.Equal(["version"], call.Args);
        Assert.Equal(["stop", "start:" + kit.Config.BinaryPath], kit.Daemon.Calls);
        if (OperatingSystem.IsWindows()) return; // no unix mode there
        Assert.Equal(Executable, File.GetUnixFileMode(kit.Config.BinaryPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(kit.Previous));
    }

    [Fact]
    public async Task An_unhealthy_linux_build_is_rolled_back_by_moving_the_previous_one_back()
    {
        kit.Dispose();
        kit = Linux(healthy: false);
        kit.InstallOld();
        var offer = kit.PublishGood();

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.RolledBack, result.Code);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
        Assert.False(File.Exists(kit.Previous), "atomic: the previous version is renamed back, not copied");
        Assert.False(File.Exists(kit.Config.BinaryPath + ".bad"), "atomic: nothing is set aside");
        Assert.Equal(offer.Version, kit.State.BlockedVersion);
    }

    [Fact]
    public async Task A_linux_service_agent_has_no_binary_of_its_own_to_replace_and_downloads_nothing()
    {
        // As on macOS and Windows (ADR-0005): the service runs an admin-owned binary outside its home; the updater replaces only the home's copy.
        kit.Dispose();
        kit = Linux(service: true);
        var offer = kit.PublishGood();

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.NotInstalled, result.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(kit.Config.BinaryPath)), "nothing is created where the binary would be");
        Assert.Empty(kit.Host.Downloads);
        Assert.Empty(kit.Runner.Calls);
        Assert.Empty(kit.Daemon.Calls);
        Assert.Equal(UpdateCodes.NotInstalled, kit.State.Result);
    }

    [Theory]
    [InlineData(OsKinds.Linux, SwapStyle.Atomic)]
    [InlineData(OsKinds.MacOs, SwapStyle.Atomic)]
    [InlineData(OsKinds.Windows, SwapStyle.RenameAside)]
    public void The_swap_style_follows_the_os_the_build_was_signed_for(string os, SwapStyle style) =>
        Assert.Equal(style, BinarySwap.For(os));

    [Fact]
    public void An_os_the_agent_does_not_know_falls_back_to_the_hosts_own_style() =>
        Assert.Equal(BinarySwap.Native, BinarySwap.For("unknown"));
}
