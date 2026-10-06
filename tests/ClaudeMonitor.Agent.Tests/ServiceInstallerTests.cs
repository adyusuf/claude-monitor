using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ServiceInstallerTests : IDisposable
{
    private readonly ServiceInstallerFixture linux = new(OsKinds.Linux);
    private readonly ServiceInstallerFixture mac = new(OsKinds.MacOs);

    public void Dispose()
    {
        linux.Dispose();
        mac.Dispose();
    }

    [Fact]
    public void Without_admin_rights_nothing_runs_and_nothing_is_created_and_the_hint_is_sudo_with_the_same_options()
    {
        var code = linux.Installer(admin: false).Install(linux.Current, ExecLevels.Argv, allowRoot: true);

        Assert.Equal(2, code);
        Assert.Contains($"The service needs root. Run:  sudo {linux.Current} install --service --exec argv --allow-root", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
        Assert.False(Directory.Exists(linux.Layout.Home));
        Assert.False(File.Exists(linux.Layout.ExecConfigPath));
    }

    [Fact]
    public void An_unknown_exec_level_is_refused_before_anything_else()
    {
        Assert.Equal(2, linux.Installer().Install(linux.Current, "everything", allowRoot: false));
        Assert.Contains("--exec takes off, argv or shell", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
    }

    [Fact]
    public void A_root_account_is_refused_without_allow_root_and_with_it_remote_execution_is_forced_off()
    {
        mac.Layout = mac.Layout with { Account = "root" };
        Assert.Equal(2, mac.Installer().Install(mac.Current, ExecLevels.Shell, allowRoot: false));
        Assert.Contains("Refusing to run the service as root", mac.Text, StringComparison.Ordinal);
        Assert.Empty(mac.Calls);
        Assert.False(File.Exists(mac.Layout.ExecConfigPath));

        Assert.Equal(0, mac.Installer().Install(mac.Current, ExecLevels.Shell, allowRoot: true));
        Assert.Contains("remote execution stays off", mac.Text, StringComparison.Ordinal);
        Assert.Equal(ExecLevels.Off, ExecPolicyLoader.ReadExisting(mac.Layout.ExecConfigPath)!.Level);
        Assert.Contains("(off)", mac.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_privileged_account_spelling_counts_as_root()
    {
        foreach (var account in new[] { "SYSTEM", "LocalSystem", @"NT AUTHORITY\SYSTEM", "Root" })
        {
            using var fx = new ServiceInstallerFixture(OsKinds.MacOs);
            fx.Layout = fx.Layout with { Account = account };
            Assert.Equal(2, fx.Installer().Install(fx.Current, ExecLevels.Argv, allowRoot: false));
        }
    }

    [Fact]
    public void Linux_without_a_systemd_run_folder_is_refused_and_nothing_is_created()
    {
        linux.RunDir = $"{linux.Dir}/no-such-folder";
        Assert.Equal(1, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Contains("does not run systemd", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
        Assert.False(Directory.Exists(linux.Layout.BinaryDir));
    }

    [Fact]
    public void A_layout_a_unit_cannot_carry_is_refused_before_anything_is_created()
    {
        linux.Layout = linux.Layout with { Home = $"{linux.Dir}/home with space" };
        Assert.Equal(1, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Contains("Not installed:", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
    }

    [Fact]
    public void A_linux_install_creates_the_account_files_policy_and_unit_and_starts_the_service_in_order()
    {
        linux.Answer = line => line.StartsWith("/usr/bin/id", StringComparison.Ordinal) ? 1 : 0; // no such account yet

        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));

        var calls = linux.Calls.ToList();
        Assert.Equal(["/usr/bin/id -u cm-agent", "/usr/sbin/useradd --system --user-group --home-dir " + linux.Layout.Home + " --no-create-home --shell /usr/sbin/nologin cm-agent"], calls.Take(2));
        Assert.Contains($"/bin/chown root:root {linux.Layout.BinaryDir}", calls);
        Assert.Contains($"/bin/chown cm-agent:cm-agent {linux.Layout.Home}", calls);
        var tail = calls.Skip(calls.IndexOf("/usr/bin/systemctl daemon-reload")).ToList();
        Assert.Equal(["/usr/bin/systemctl daemon-reload", "/usr/bin/systemctl enable --now cm-agent"], tail);

        Assert.Equal("binary-v2", File.ReadAllText(linux.Layout.Binary));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, File.GetUnixFileMode(linux.Layout.Binary));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(linux.Layout.Home));
        Assert.Equal(ExecLevels.Argv, ExecPolicyLoader.ReadExisting(linux.Layout.ExecConfigPath)!.Level);
        Assert.Equal(SystemdUnit.Render(linux.Layout), File.ReadAllText(linux.Definition));
        Assert.Empty(Directory.GetFiles(linux.Layout.BinaryDir, "*.tmp"));
        Assert.Contains("The service is installed and started.", linux.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_that_exists_is_reused_and_an_upgrade_restarts_the_service_on_the_new_binary()
    {
        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.DoesNotContain("/usr/sbin/useradd", linux.Calls.Select(c => c.Split(' ')[0]));
        Assert.False(linux.Called("/usr/bin/systemctl restart"));

        File.WriteAllText(linux.Current, "binary-v3");
        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.True(linux.Called("/usr/bin/systemctl restart cm-agent"));
        Assert.Equal("binary-v3", File.ReadAllText(linux.Layout.Binary));
    }

    [Fact]
    public void A_reinstall_keeps_the_ceiling_an_admin_wrote_by_hand_and_changes_only_the_level()
    {
        ExecPolicyLoader.Save(linux.Layout.ExecConfigPath, new ExecPolicy(ExecLevels.Argv, ["/usr/bin/true"], ["/srv/app", "/var/log/app"]));

        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Shell, allowRoot: false));

        var policy = ExecPolicyLoader.ReadExisting(linux.Layout.ExecConfigPath)!;
        Assert.Equal(ExecLevels.Shell, policy.Level);
        Assert.Equal(["/usr/bin/true"], policy.AllowedExecutables);
        Assert.Equal(["/srv/app", "/var/log/app"], policy.AllowedRoots);
    }

    [Fact]
    public void A_first_install_has_an_empty_ceiling_and_an_unreadable_old_policy_is_replaced_by_one_without_it()
    {
        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        var first = ExecPolicyLoader.ReadExisting(linux.Layout.ExecConfigPath)!;
        Assert.Equal((0, 0), (first.AllowedExecutables.Count, first.AllowedRoots.Count));

        File.WriteAllText(linux.Layout.ExecConfigPath, """{"level":"argv","allowedRoots":["relative"]}""");
        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Off, allowRoot: false));
        Assert.Empty(ExecPolicyLoader.ReadExisting(linux.Layout.ExecConfigPath)!.AllowedRoots);
    }

    [Fact]
    public void A_failing_step_stops_the_install_says_what_failed_and_undoes_everything_this_run_created()
    {
        linux.Answer = line => line.StartsWith("/usr/bin/id", StringComparison.Ordinal) ? 1 : line.StartsWith("/usr/bin/systemctl enable", StringComparison.Ordinal) ? 5 : 0;

        Assert.Equal(1, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));

        Assert.Contains("Install failed: could not enable and start the service: systemctl exited with 5.", linux.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(linux.Layout.Binary), "the binary this run copied is removed");
        Assert.False(Directory.Exists(linux.Layout.BinaryDir));
        Assert.False(Directory.Exists(linux.Layout.Home));
        Assert.False(File.Exists(linux.Layout.ExecConfigPath));
        Assert.False(File.Exists(linux.Definition));
        Assert.True(linux.Called("/usr/sbin/userdel cm-agent"), "the account this run created is removed");
        Assert.Contains("Undone: service definition written.", linux.Text, StringComparison.Ordinal);
        Assert.Contains("Undone: account created.", linux.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("The service is installed", linux.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void What_existed_before_a_failed_install_is_not_removed()
    {
        Directory.CreateDirectory(linux.Layout.BinaryDir);
        Directory.CreateDirectory(linux.Layout.Home);
        File.WriteAllText(linux.Layout.Binary, "old-binary");
        linux.Answer = line => line.StartsWith("/usr/bin/systemctl enable", StringComparison.Ordinal) ? 5 : 0;

        Assert.Equal(1, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));

        Assert.True(Directory.Exists(linux.Layout.BinaryDir));
        Assert.True(Directory.Exists(linux.Layout.Home));
        Assert.True(File.Exists(linux.Layout.Binary));
        Assert.False(linux.Called("/usr/sbin/userdel"), "the account existed (id answered 0)");
    }

    [Fact]
    public void A_missing_tool_is_named_as_not_found_and_the_install_is_undone()
    {
        linux.Answer = line => line.StartsWith("/usr/bin/id", StringComparison.Ordinal) ? 1 : line.StartsWith("/usr/sbin/useradd", StringComparison.Ordinal) ? ServiceInstaller.ToolNotFound : 0;
        Assert.Equal(1, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Contains("/usr/sbin/useradd was not found.", linux.Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(linux.Layout.BinaryDir));
    }

    [Fact]
    public void A_home_or_binary_folder_that_is_a_symbolic_link_is_never_used()
    {
        var elsewhere = Directory.CreateDirectory($"{linux.Dir}/elsewhere").FullName;
        Directory.CreateSymbolicLink(linux.Layout.Home, elsewhere);
        Assert.Equal(1, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Contains("is a symbolic link; it is not used.", linux.Text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));

        using var other = new ServiceInstallerFixture(OsKinds.Linux);
        var target = Directory.CreateDirectory($"{other.Dir}/elsewhere").FullName;
        Directory.CreateSymbolicLink(other.Layout.BinaryDir, target);
        Assert.Equal(1, other.Installer().Install(other.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Empty(Directory.GetFileSystemEntries(target)); // nothing was copied through the link
    }

    // ---- macOS -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_mac_install_creates_the_role_account_with_the_highest_free_id_and_loads_the_daemon()
    {
        mac.Answer = line => line.StartsWith("/usr/bin/dscl . -read", StringComparison.Ordinal) ? 1 : 0;
        mac.DsclList = "root 0\n_other 399\n_x 398\nnobody -2\n";

        Assert.Equal(0, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));

        Assert.Contains("/usr/bin/dscl . -create /Users/_cmagent UniqueID 397", mac.Calls);
        Assert.Contains("/usr/bin/dscl . -create /Groups/_cmagent PrimaryGroupID 397", mac.Calls);
        Assert.Contains($"/usr/sbin/chown root:wheel {mac.Layout.BinaryDir}", mac.Calls);
        Assert.Contains($"/usr/sbin/chown _cmagent:_cmagent {mac.Layout.Home}", mac.Calls);
        Assert.Equal($"/bin/launchctl bootstrap system {mac.Definition}", mac.Calls.Last());
        Assert.Equal(LaunchdDaemon.Render(mac.Layout), File.ReadAllText(mac.Definition));
        Assert.False(mac.Called("/bin/launchctl bootout"), "a first install has no old copy to unload");
    }

    [Fact]
    public void A_mac_upgrade_unloads_the_old_daemon_first_and_an_existing_account_is_reused()
    {
        Assert.Equal(0, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));
        mac.Calls.Clear();
        Assert.Equal(0, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));
        var calls = mac.Calls.ToList();
        Assert.True(calls.IndexOf($"/bin/launchctl bootout system/{LaunchdDaemon.Label}") < calls.IndexOf($"/bin/launchctl bootstrap system {mac.Definition}"));
        Assert.DoesNotContain(calls, c => c.Contains("-create /Users", StringComparison.Ordinal));
    }

    [Fact]
    public void When_the_existing_ids_cannot_be_listed_or_none_is_free_the_install_stops_before_creating_the_account()
    {
        mac.Answer = line => line.StartsWith("/usr/bin/dscl . -read", StringComparison.Ordinal) ? 1 : 0;
        mac.DsclList = null;
        Assert.Equal(1, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Contains("could not list the existing users", mac.Text, StringComparison.Ordinal);

        mac.DsclList = string.Join('\n', Enumerable.Range(200, 200).Select(i => $"_u{i} {i}"));
        Assert.Equal(1, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Contains("no free user and group id between 200 and 399", mac.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(mac.Calls, c => c.Contains("-create /Users", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_mac_load_removes_the_plist_and_deletes_the_half_made_account()
    {
        mac.Answer = line => line.StartsWith("/usr/bin/dscl . -read", StringComparison.Ordinal) ? 1 : line.StartsWith("/bin/launchctl bootstrap", StringComparison.Ordinal) ? 1 : 0;
        Assert.Equal(1, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));
        Assert.False(File.Exists(mac.Definition));
        Assert.Contains("/usr/bin/dscl . -delete /Users/_cmagent", mac.Calls);
        Assert.Contains("/usr/bin/dscl . -delete /Groups/_cmagent", mac.Calls);
    }
}
