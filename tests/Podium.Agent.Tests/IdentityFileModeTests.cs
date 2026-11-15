using System.Runtime.Versioning;
using Podium.Agent.Auth;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;

namespace Podium.Agent.Tests;

/// <summary>F-325: the home holds the machine key, the local database and the log; only its owner may read any of it (ADR-0002).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class IdentityFileModeTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    [Fact]
    public void Ensure_home_makes_the_home_owner_only_even_when_it_already_existed_open()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(home.Config.Home, OwnerOnlyFolder | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        home.Config.EnsureHome();

        Assert.Equal(OwnerOnlyFolder, File.GetUnixFileMode(home.Config.Home));
    }

    [Fact]
    public void A_first_login_writes_agent_json_with_no_group_or_other_bits()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(File.Exists(home.Config.IdentityPath));

        Assert.True(Identity.Load(home.Config).HasMachineKey);

        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(home.Config.IdentityPath));
        Assert.Equal(OwnerOnlyFolder, File.GetUnixFileMode(home.Config.Home));
    }

    [Fact]
    public void A_save_keeps_agent_json_owner_only_when_it_creates_and_when_it_replaces_the_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var identity = Identity.Load(home.Config);

        (identity with { Server = "https://aipod.invalid" }).Save(home.Config);
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(home.Config.IdentityPath));

        File.Delete(home.Config.IdentityPath);
        identity.Save(home.Config);
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(home.Config.IdentityPath));
        Assert.Equal(identity.MachineKey, Identity.Load(home.Config).MachineKey);
    }

    [Fact]
    public void The_database_and_the_log_live_in_a_home_no_one_else_can_enter()
    {
        if (OperatingSystem.IsWindows()) return;
        using (var store = new LocalStore(home.Config.DatabasePath)) store.Set("probe", "1");
        new AgentLog(home.Config, TimeProvider.System).Write("probe");

        Assert.True(File.Exists(home.Config.DatabasePath));
        Assert.True(File.Exists(home.Config.LogPath));
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(home.Config.Home) & GroupOrOther);
    }
}
