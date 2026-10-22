using System.Runtime.Versioning;
using Podium.Agent.Install;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>"install --standalone" and "uninstall --standalone" (ADR-0008 §3g): login first, the user's own account, files written atomically, every tool through a seam, undo on failure.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class StandaloneInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ap-sa-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly List<(string Tool, string[] Args)> calls = [];
    private readonly Dictionary<string, int> exits = [];
    private readonly List<string?> logins = [];
    private bool connected = true;
    private bool loginSucceeds = true;
    private string? linger;

    public StandaloneInstallerTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private string Profile => Path.Combine(root, "profile");
    private string Home => Path.Combine(Profile, ".aipod");
    private string Current => Path.Combine(root, "downloaded", "aipod");

    private StandaloneLayout Layout(string os) => StandaloneLayout.For(os, Profile, Home, "ayse");

    private StandaloneLayout AipodLayout(string os) => StandaloneLayout.ForAipod(os, Profile, Home, "ayse");

    private StandaloneInstaller Installer(string os, bool isRoot = false, StandaloneLayout? layout = null, StandaloneLayout? legacy = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Current)!);
        File.WriteAllText(Current, "binary");
        return new StandaloneInstaller(output, (tool, args) =>
        {
            calls.Add((tool, [.. args]));
            return exits.GetValueOrDefault(Path.GetFileName(tool) + " " + (args.Count > 0 ? args[0] == "--user" && args.Count > 1 ? args[1] : args[0] : ""), 0);
        }, (_, _) => linger, layout ?? Layout(os), isRoot, () => connected, async server =>
        {
            logins.Add(server);
            if (loginSucceeds) connected = true;
            return loginSucceeds ? 0 : 1;
        }, () => 501u, "0.3.0", legacy);
    }

    private string Out => output.ToString();
    private bool Called(string toolName, params string[] firstArgs) => calls.Any(c => Path.GetFileName(c.Tool) == toolName && c.Args.Take(firstArgs.Length).SequenceEqual(firstArgs));

    [Fact]
    public async Task A_root_account_is_refused_and_nothing_is_written()
    {
        var code = await Installer(OsKinds.Linux, isRoot: true).InstallAsync(Current, null);
        Assert.Equal(2, code);
        Assert.Contains("not as root", Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Home));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task On_linux_the_binary_is_copied_the_user_unit_written_and_enabled_now_in_the_users_own_systemd()
    {
        var code = await Installer(OsKinds.Linux).InstallAsync(Current, null);

        Assert.Equal(0, code);
        var l = Layout(OsKinds.Linux);
        Assert.Equal("binary", File.ReadAllText(l.Binary));
        Assert.True(File.GetUnixFileMode(l.Binary).HasFlag(UnixFileMode.UserExecute));
        Assert.Contains($"ExecStart={l.AutostartBinary} daemon", File.ReadAllText(l.DefinitionPath), StringComparison.Ordinal);
        Assert.True(Called("systemctl", "--user", "show-environment"));
        Assert.True(Called("systemctl", "--user", "daemon-reload"));
        Assert.True(Called("systemctl", "--user", "enable", "--now", "aipod.service"));
        Assert.False(File.Exists(l.DefinitionPath + ".tmp"));
        Assert.Empty(logins); // already connected: no login
    }

    [Fact]
    public async Task On_macos_the_binary_goes_into_the_application_bundle_with_its_plist_and_the_launch_agent_is_loaded_in_the_users_gui_domain()
    {
        var code = await Installer(OsKinds.MacOs).InstallAsync(Current, null);

        Assert.Equal(0, code);
        var l = Layout(OsKinds.MacOs);
        Assert.Equal("binary", File.ReadAllText(l.AutostartBinary));
        Assert.Contains("LSUIElement", File.ReadAllText(l.InfoPlistPath), StringComparison.Ordinal);
        Assert.Contains("com.aipod.agent.user", File.ReadAllText(l.DefinitionPath), StringComparison.Ordinal);
        Assert.True(Called("launchctl", "bootstrap", "gui/501", l.DefinitionPath));
        Assert.True(Called("launchctl", "kickstart", "-k", "gui/501/com.aipod.agent.user"));
    }

    [Fact]
    public async Task Not_connected_without_a_server_asks_for_one_and_changes_nothing()
    {
        connected = false;
        var code = await Installer(OsKinds.Linux).InstallAsync(Current, null);
        Assert.Equal(2, code);
        Assert.Contains("--server", Out, StringComparison.Ordinal);
        Assert.Empty(logins);
        Assert.False(File.Exists(Layout(OsKinds.Linux).DefinitionPath));
        Assert.False(Called("systemctl", "--user", "enable"));
    }

    [Fact]
    public async Task Not_connected_with_a_server_logs_in_first_and_only_then_enables_the_autostart()
    {
        connected = false;
        var code = await Installer(OsKinds.Linux).InstallAsync(Current, "https://podium.example.test");

        Assert.Equal(0, code);
        Assert.Equal(["https://podium.example.test"], logins);
        Assert.True(Called("systemctl", "--user", "enable", "--now", "aipod.service"));
    }

    [Fact]
    public async Task A_login_that_does_not_complete_enables_nothing_and_writes_no_definition()
    {
        connected = false;
        loginSucceeds = false;
        var code = await Installer(OsKinds.Linux).InstallAsync(Current, "https://podium.example.test");

        Assert.Equal(1, code);
        Assert.Contains("the login did not complete", Out, StringComparison.Ordinal);
        Assert.False(File.Exists(Layout(OsKinds.Linux).DefinitionPath));
        Assert.False(File.Exists(Layout(OsKinds.Linux).Binary));
        Assert.False(Called("systemctl", "--user", "enable"));
    }

    [Fact]
    public async Task A_machine_without_a_systemd_user_session_is_refused_before_anything_is_written()
    {
        exits["systemctl show-environment"] = 1;
        var code = await Installer(OsKinds.Linux).InstallAsync(Current, null);
        Assert.Equal(1, code);
        Assert.Contains("no systemd user session", Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Home));
    }

    [Fact]
    public async Task A_home_with_a_space_is_refused_for_the_unit_before_anything_is_written()
    {
        var l = StandaloneLayout.For(OsKinds.Linux, Profile, Path.Combine(Profile, "a b"), "ayse");
        var code = await Installer(OsKinds.Linux, layout: l).InstallAsync(Current, null);
        Assert.Equal(1, code);
        Assert.Contains("needs quoting", Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Profile, "a b")));
    }

    [Fact]
    public async Task A_step_that_fails_stops_the_install_and_undoes_what_this_run_created_but_keeps_a_binary_that_was_already_there()
    {
        var l = Layout(OsKinds.Linux);
        Directory.CreateDirectory(Path.GetDirectoryName(l.Binary)!);
        File.WriteAllText(l.Binary, "older binary");
        exits["systemctl enable"] = 1;

        var code = await Installer(OsKinds.Linux).InstallAsync(Current, null);

        Assert.Equal(1, code);
        Assert.Contains("could not enable and start the unit", Out, StringComparison.Ordinal);
        Assert.Contains("Undone", Out, StringComparison.Ordinal);
        Assert.False(File.Exists(l.DefinitionPath));
        Assert.Equal("binary", File.ReadAllText(l.Binary)); // overwritten by the install, not deleted by the undo
    }

    [Fact]
    public async Task A_failure_on_a_clean_machine_leaves_no_binary_and_no_unit_behind()
    {
        exits["systemctl enable"] = 1;
        var code = await Installer(OsKinds.Linux).InstallAsync(Current, null);
        Assert.Equal(1, code);
        var l = Layout(OsKinds.Linux);
        Assert.False(File.Exists(l.Binary));
        Assert.False(File.Exists(l.DefinitionPath));
    }

    [Fact]
    public async Task Without_lingering_the_install_says_how_to_keep_the_agent_running_after_logout()
    {
        linger = "Linger=no\n";
        await Installer(OsKinds.Linux).InstallAsync(Current, null);
        Assert.Contains("loginctl enable-linger ayse", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_install_says_the_machine_reports_no_claude()
    {
        await Installer(OsKinds.Linux).InstallAsync(Current, null);
        Assert.Contains("claude=none", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_on_linux_stops_the_unit_removes_its_file_and_keeps_the_home_and_the_binary()
    {
        await Installer(OsKinds.Linux).InstallAsync(Current, null);
        calls.Clear();
        output.GetStringBuilder().Clear();

        var code = Installer(OsKinds.Linux).Uninstall();

        Assert.Equal(0, code);
        var l = Layout(OsKinds.Linux);
        Assert.True(Called("systemctl", "--user", "disable", "--now", "aipod.service"));
        Assert.False(File.Exists(l.DefinitionPath));
        Assert.True(File.Exists(l.Binary));
        Assert.True(Directory.Exists(Home));
        Assert.Contains("Kept: the home", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_on_macos_unloads_the_launch_agent_and_removes_it_and_the_bundle()
    {
        await Installer(OsKinds.MacOs).InstallAsync(Current, null);
        calls.Clear();

        Assert.Equal(0, Installer(OsKinds.MacOs).Uninstall());

        var l = Layout(OsKinds.MacOs);
        Assert.True(Called("launchctl", "bootout", "gui/501/com.aipod.agent.user"));
        Assert.False(File.Exists(l.DefinitionPath));
        Assert.False(Directory.Exists(l.BundlePath!));
        Assert.True(File.Exists(l.Binary));
    }

    [Fact]
    public void Uninstall_when_nothing_is_installed_says_so_and_runs_no_tool()
    {
        Assert.Equal(0, Installer(OsKinds.Linux).Uninstall());
        Assert.Contains("is not installed", Out, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_reinstall_replaces_the_files_and_the_loaded_launch_agent()
    {
        await Installer(OsKinds.MacOs).InstallAsync(Current, null);
        var again = Installer(OsKinds.MacOs); // writes the downloaded binary; the newer one is written after it
        File.WriteAllText(Current, "newer binary");
        calls.Clear();

        Assert.Equal(0, await again.InstallAsync(Current, null));

        Assert.Equal("newer binary", File.ReadAllText(Layout(OsKinds.MacOs).AutostartBinary));
        Assert.True(Called("launchctl", "bootout", "gui/501/com.aipod.agent.user")); // what was loaded is replaced
        Assert.True(Called("launchctl", "bootstrap", "gui/501"));
    }

    // ---- the aipod names (ADR-0013, tier 2) -------------------------------------------------------------------------

    [Fact]
    public void The_aipod_layout_has_its_own_label_unit_task_bundle_and_definition_paths_and_the_old_one_is_what_it_was()
    {
        Assert.Equal(("com.aipod.agent.user", "aipod.service", "Aipod", "Aipod.app"), (Layout(OsKinds.MacOs).Names.Label, Layout(OsKinds.Linux).Names.UnitName, Layout(OsKinds.Windows).Names.TaskName, Layout(OsKinds.MacOs).Names.BundleName));
        var mac = AipodLayout(OsKinds.MacOs);
        Assert.Equal(("aipod.agent.user", "Aipod.app", "aipod.agent.app"), (mac.Names.Label, mac.Names.BundleName, mac.Names.BundleId));
        Assert.Equal($"{Profile}/Library/LaunchAgents/aipod.agent.user.plist", mac.DefinitionPath);
        Assert.Equal($"{Home}/Aipod.app/Contents/MacOS/aipod", mac.AutostartBinary);
        Assert.Equal($"{Profile}/.config/systemd/user/aipod.service", AipodLayout(OsKinds.Linux).DefinitionPath);
        Assert.Equal($@"{Home}\AipodAgent.task.xml", AipodLayout(OsKinds.Windows).DefinitionPath);
        Assert.Equal(Layout(OsKinds.Linux).Binary, AipodLayout(OsKinds.Linux).Binary); // the binary copy keeps its place and its name
    }

    [Fact]
    public async Task An_install_with_the_aipod_names_registers_under_them_on_linux_and_macos()
    {
        Assert.Equal(0, await Installer(OsKinds.Linux, layout: AipodLayout(OsKinds.Linux)).InstallAsync(Current, null));
        Assert.True(Called("systemctl", "--user", "enable", "--now", "aipod.service"));
        Assert.True(File.Exists(AipodLayout(OsKinds.Linux).DefinitionPath));

        calls.Clear();
        Assert.Equal(0, await Installer(OsKinds.MacOs, layout: AipodLayout(OsKinds.MacOs)).InstallAsync(Current, null));
        var l = AipodLayout(OsKinds.MacOs);
        Assert.Contains("<key>Label</key><string>aipod.agent.user</string>", File.ReadAllText(l.DefinitionPath), StringComparison.Ordinal);
        Assert.Contains("aipod.agent.app", File.ReadAllText(l.InfoPlistPath), StringComparison.Ordinal);
        Assert.True(Called("launchctl", "kickstart", "-k", "gui/501/aipod.agent.user"));
    }

    [Fact]
    public async Task An_install_since_the_rename_replaces_a_registration_under_the_old_names_before_it_enables_the_new_one()
    {
        await Installer(OsKinds.Linux).InstallAsync(Current, null); // an agent installed before the rename
        Assert.True(File.Exists(Layout(OsKinds.Linux).DefinitionPath));
        calls.Clear();
        output.GetStringBuilder().Clear();

        Assert.Equal(0, await Installer(OsKinds.Linux, layout: AipodLayout(OsKinds.Linux), legacy: Layout(OsKinds.Linux)).InstallAsync(Current, null));

        Assert.False(File.Exists(Layout(OsKinds.Linux).DefinitionPath));
        Assert.True(File.Exists(AipodLayout(OsKinds.Linux).DefinitionPath));
        var order = calls.Select(c => string.Join(' ', c.Args)).ToList();
        Assert.True(order.IndexOf("--user disable --now aipod.service") < order.IndexOf("--user enable --now aipod.service"));
        Assert.Contains("Replaced the autostart that had the old names", Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_macos_the_old_launch_agent_and_bundle_are_unloaded_and_removed_when_the_new_ones_replace_them()
    {
        await Installer(OsKinds.MacOs).InstallAsync(Current, null);
        var old = Layout(OsKinds.MacOs);
        calls.Clear();

        Assert.Equal(0, await Installer(OsKinds.MacOs, layout: AipodLayout(OsKinds.MacOs), legacy: old).InstallAsync(Current, null));

        Assert.True(Called("launchctl", "bootout", "gui/501/com.aipod.agent.user"));
        Assert.False(File.Exists(old.DefinitionPath));
        Assert.False(Directory.Exists(old.BundlePath!));
        Assert.True(Directory.Exists(AipodLayout(OsKinds.MacOs).BundlePath!));
    }

    [Fact]
    public async Task Uninstall_removes_the_registration_under_either_set_of_names_and_says_nothing_is_installed_only_when_neither_is()
    {
        await Installer(OsKinds.Linux, layout: AipodLayout(OsKinds.Linux)).InstallAsync(Current, null);
        calls.Clear();
        Assert.Equal(0, Installer(OsKinds.Linux, layout: AipodLayout(OsKinds.Linux), legacy: Layout(OsKinds.Linux)).Uninstall());
        Assert.True(Called("systemctl", "--user", "disable", "--now", "aipod.service"));
        Assert.False(File.Exists(AipodLayout(OsKinds.Linux).DefinitionPath));

        await Installer(OsKinds.Linux).InstallAsync(Current, null); // an old one only
        calls.Clear();
        Assert.Equal(0, Installer(OsKinds.Linux, layout: AipodLayout(OsKinds.Linux), legacy: Layout(OsKinds.Linux)).Uninstall());
        Assert.True(Called("systemctl", "--user", "disable", "--now", "aipod.service"));
        Assert.False(File.Exists(Layout(OsKinds.Linux).DefinitionPath));

        output.GetStringBuilder().Clear();
        calls.Clear();
        Assert.Equal(0, Installer(OsKinds.Linux, layout: AipodLayout(OsKinds.Linux), legacy: Layout(OsKinds.Linux)).Uninstall());
        Assert.Contains("is not installed", Out, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, c => c.Args.Contains("disable"));
    }
}
