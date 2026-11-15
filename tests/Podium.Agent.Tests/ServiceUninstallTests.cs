using System.Runtime.Versioning;
using Podium.Agent.Install;
using Podium.Contracts;

namespace Podium.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ServiceUninstallTests : IDisposable
{
    private readonly ServiceInstallerFixture linux = new(OsKinds.Linux);
    private readonly ServiceInstallerFixture mac = new(OsKinds.MacOs);

    public void Dispose()
    {
        linux.Dispose();
        mac.Dispose();
    }

    // ---- uninstall ---------------------------------------------------------------------------------------------

    [Fact]
    public void Uninstalling_when_nothing_is_installed_succeeds_and_runs_nothing()
    {
        Assert.Equal(0, linux.Installer().Uninstall());
        Assert.Contains("The service is not installed.", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
        Assert.Equal(0, mac.Installer().Uninstall());
        Assert.Empty(mac.Calls);
    }

    private static ServiceInstaller WithSudoers(ServiceInstallerFixture fx) =>
        new(fx.Output, fx.Run, fx.Os, true, fx.Layout, fx.Definition, fx.RunDir, (_, _) => fx.DsclList) { SudoersFolder = $"{fx.Dir}/sudoers.d" };

    [Fact]
    public void A_sudoers_drop_in_left_without_a_service_definition_is_still_removed_and_the_group_left_behind_is_named()
    {
        foreach (var fx in new[] { linux, mac })
        {
            Assert.Equal(0, WithSudoers(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "ap-run"));
            File.Delete(fx.Definition); // a partial earlier uninstall, or a hand-removed unit
            fx.Calls.Clear();

            Assert.Equal(0, WithSudoers(fx).Uninstall());

            Assert.False(File.Exists($"{fx.Dir}/sudoers.d/aipod-run"), $"{fx.Os}: the drop-in still lets the service act as the run account");
            Assert.Contains("The service is not installed.", fx.Text, StringComparison.Ordinal);
            Assert.Contains("Removed the sudoers drop-in", fx.Text, StringComparison.Ordinal);
            Assert.Contains("aipod stays in the group of the run account ap-run", fx.Text, StringComparison.Ordinal);
            Assert.Empty(fx.Calls); // no service manager is asked about a service that is not there
        }
    }

    [Fact]
    public void A_full_uninstall_names_the_group_membership_it_leaves()
    {
        Assert.Equal(0, WithSudoers(linux).Install(linux.Current, ExecLevels.Argv, allowRoot: false, runAccount: "ap-run"));

        Assert.Equal(0, WithSudoers(linux).Uninstall());

        Assert.Contains("aipod stays in the group of the run account ap-run", linux.Text, StringComparison.Ordinal);
        Assert.Contains("The service is removed. Kept:", linux.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_run_account_nothing_is_said_about_a_group()
    {
        Assert.Equal(0, WithSudoers(linux).Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Equal(0, WithSudoers(linux).Uninstall());
        Assert.DoesNotContain("group of the run account", linux.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstalling_without_admin_rights_asks_for_root()
    {
        Assert.Equal(2, linux.Installer(admin: false).Uninstall());
        Assert.Contains("uninstall --service", linux.Text, StringComparison.Ordinal);
        Assert.Contains("sudo", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
    }

    [Fact]
    public void Uninstalling_stops_the_service_removes_its_definition_and_keeps_the_home_policy_and_binary()
    {
        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        linux.Calls.Clear();

        Assert.Equal(0, linux.Installer().Uninstall());

        Assert.Equal(["/usr/bin/systemctl disable --now aipod", "/usr/bin/systemctl daemon-reload"], linux.Calls);
        Assert.False(File.Exists(linux.Definition));
        Assert.True(Directory.Exists(linux.Layout.Home));
        Assert.True(File.Exists(linux.Layout.ExecConfigPath));
        Assert.True(File.Exists(linux.Layout.Binary));
        Assert.Contains("The service is removed. Kept:", linux.Text, StringComparison.Ordinal);

        Assert.Equal(0, mac.Installer().Install(mac.Current, ExecLevels.Argv, allowRoot: false));
        mac.Calls.Clear();
        Assert.Equal(0, mac.Installer().Uninstall());
        Assert.Equal([$"/bin/launchctl bootout system/{ServiceNames.Default.Label}"], mac.Calls);
        Assert.False(File.Exists(mac.Definition));
    }

    [Fact]
    public void A_failing_stop_leaves_the_definition_in_place_and_reports_it()
    {
        Assert.Equal(0, linux.Installer().Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        linux.Answer = line => line.StartsWith("/usr/bin/systemctl disable", StringComparison.Ordinal) ? 4 : 0;
        Assert.Equal(1, linux.Installer().Uninstall());
        Assert.True(File.Exists(linux.Definition));
        Assert.Contains("Uninstall failed: could not stop and disable the service", linux.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_os_without_a_service_manager_support_is_refused()
    {
        var unsupported = new ServiceInstaller(linux.Output, linux.Run, "plan9", isAdmin: true, linux.Layout);
        Assert.Equal(2, unsupported.Install(linux.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Equal(2, unsupported.Uninstall());
        Assert.Contains("A service is not supported on this OS (plan9).", linux.Text, StringComparison.Ordinal);
        Assert.Empty(linux.Calls);
    }

    [Fact]
    public void The_default_tool_runner_reports_an_exit_code_and_a_missing_tool_as_127()
    {
        Assert.Equal(0, ServiceInstaller.RunTool("/usr/bin/true", []));
        Assert.Equal(1, ServiceInstaller.RunTool("/usr/bin/false", []));
        Assert.Equal(ServiceInstaller.ToolNotFound, ServiceInstaller.RunTool($"{linux.Dir}/no-such-tool", []));
        Assert.Null(ServiceInstaller.CaptureTool($"{linux.Dir}/no-such-tool", []));
        Assert.Null(ServiceInstaller.CaptureTool("/usr/bin/false", []));
        Assert.StartsWith("hello", ServiceInstaller.CaptureTool("/bin/echo", ["hello"]), StringComparison.Ordinal);
    }
}
