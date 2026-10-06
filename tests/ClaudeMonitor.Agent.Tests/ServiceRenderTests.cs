using System.Xml.Linq;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class ServiceRenderTests
{
    private static readonly ServiceLayout Linux = ServiceLayout.For(OsKinds.Linux);
    private static readonly ServiceLayout Mac = ServiceLayout.For(OsKinds.MacOs);
    private static readonly ServiceLayout Windows = ServiceLayout.For(OsKinds.Windows, @"C:\Program Files", @"C:\ProgramData");

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---- systemd ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("NoNewPrivileges=yes")]
    [InlineData("ProtectSystem=strict")]
    [InlineData("ProtectHome=yes")]
    [InlineData("ReadWritePaths=/var/lib/cm-agent")]
    [InlineData("PrivateTmp=yes")]
    [InlineData("PrivateDevices=yes")]
    [InlineData("CapabilityBoundingSet=")]
    [InlineData("AmbientCapabilities=")]
    [InlineData("RestrictSUIDSGID=yes")]
    [InlineData("LockPersonality=yes")]
    [InlineData("UMask=0077")]
    [InlineData("KillMode=control-group")]
    [InlineData("RestartPreventExitStatus=1")]
    [InlineData("Restart=on-failure")]
    [InlineData("Environment=CM_SERVICE=1")]
    [InlineData("Environment=CM_AGENT_HOME=/var/lib/cm-agent")]
    [InlineData("Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/cm-agent/.net")]
    [InlineData("User=cm-agent")]
    [InlineData("Group=cm-agent")]
    [InlineData("ExecStart=/usr/local/lib/cm-agent/cm-agent daemon")]
    [InlineData("MemoryMax=1G")]
    [InlineData("TasksMax=256")]
    [InlineData("CPUQuota=50%")]
    [InlineData("WantedBy=multi-user.target")]
    public void The_systemd_unit_carries_every_hardening_and_service_directive(string line) =>
        Assert.Contains(line, Lines(SystemdUnit.Render(Linux)));

    [Fact]
    public void The_server_is_in_the_unit_only_when_one_is_set()
    {
        Assert.DoesNotContain("CM_SERVER", SystemdUnit.Render(Linux), StringComparison.Ordinal);
        Assert.Contains("Environment=CM_SERVER=https://monitor.invalid", Lines(SystemdUnit.Render(Linux with { Server = "https://monitor.invalid" })));
    }

    [Theory]
    [InlineData("/var/lib/cm agent")]
    [InlineData("/var/lib/cm-agent\nExecStartPost=/bin/evil")]
    [InlineData("/var/lib/cm-agent\r")]
    [InlineData("/var/lib/%h")]
    [InlineData("/var/lib/$HOME")]
    [InlineData("/var/lib/\"quoted\"")]
    [InlineData("/var/lib/a;b")]
    [InlineData("/var/../etc")]
    [InlineData("relative/path")]
    [InlineData("/onlyone")]
    public void A_path_that_would_need_quoting_or_could_add_a_directive_is_refused(string home)
    {
        var layout = Linux with { Home = home };
        Assert.NotNull(SystemdUnit.Check(layout));
        Assert.Throws<ArgumentException>(() => SystemdUnit.Render(layout));
        Assert.Throws<ArgumentException>(() => SystemdUnit.Render(Linux with { Binary = home }));
    }

    [Theory]
    [InlineData("Root")]
    [InlineData("cm agent")]
    [InlineData("cm-agent\nUser=root")]
    [InlineData("")]
    public void An_account_name_that_is_not_a_plain_system_name_is_refused(string account) =>
        Assert.NotNull(SystemdUnit.Check(Linux with { Account = account }));

    [Fact]
    public void The_account_is_created_locked_with_its_own_group_and_no_login_shell()
    {
        Assert.Equal(["--system", "--user-group", "--home-dir", "/var/lib/cm-agent", "--no-create-home", "--shell", "/usr/sbin/nologin", "cm-agent"], SystemdUnit.UserAddArgs(Linux));
        Assert.Null(SystemdUnit.Check(Linux));
    }

    // ---- launchd ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_launch_daemon_runs_as_the_role_account_with_umask_077_and_the_service_environment()
    {
        var doc = XDocument.Parse(LaunchdDaemon.Render(Mac with { Server = "https://monitor.invalid" }).Replace("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">", "", StringComparison.Ordinal));
        var dict = doc.Root!.Element("dict")!;
        var values = dict.Elements().Chunk(2).ToDictionary(p => p[0].Value, p => p[1]);

        Assert.Equal("_cmagent", values["UserName"].Value);
        Assert.Equal("_cmagent", values["GroupName"].Value);
        Assert.Equal("63", values["Umask"].Value);
        Assert.Equal("com.claudemonitor.agent", values["Label"].Value);
        Assert.Equal([Mac.Binary, "daemon"], values["ProgramArguments"].Elements("string").Select(e => e.Value));
        Assert.Equal("true", values["RunAtLoad"].Name.LocalName);
        Assert.Equal(["SuccessfulExit", "false"], values["KeepAlive"].Elements().Select(e => e.Name.LocalName == "key" ? e.Value : e.Name.LocalName)); // restart only after a failure
        var env = values["EnvironmentVariables"].Elements().Chunk(2).ToDictionary(p => p[0].Value, p => p[1].Value);
        Assert.Equal(("1", Mac.Home, "https://monitor.invalid"), (env["CM_SERVICE"], env["CM_AGENT_HOME"], env["CM_SERVER"]));
    }

    [Fact]
    public void Text_that_is_xml_is_escaped_and_a_character_xml_cannot_carry_is_refused()
    {
        var text = LaunchdDaemon.Render(Mac with { Server = "https://x.invalid/?a=1&b=<2>" });
        Assert.Contains("https://x.invalid/?a=1&amp;b=&lt;2&gt;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<2>", text, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => LaunchdDaemon.Render(Mac with { Server = "https://x.invalid/\u0001" }));
        Assert.Throws<ArgumentException>(() => LaunchdDaemon.Render(Mac with { Binary = "relative/cm-agent" }));
        Assert.Throws<ArgumentException>(() => LaunchdDaemon.Render(Mac with { Home = "relative" }));
    }

    [Fact]
    public void The_hidden_role_account_and_its_group_get_the_same_id_a_locked_password_and_the_service_home()
    {
        var commands = LaunchdDaemon.AccountCommands(Mac, 301).Select(c => string.Join(' ', c)).ToList();
        Assert.Contains(". -create /Groups/_cmagent PrimaryGroupID 301", commands);
        Assert.Contains(". -create /Users/_cmagent UniqueID 301", commands);
        Assert.Contains(". -create /Users/_cmagent PrimaryGroupID 301", commands);
        Assert.Contains(". -create /Users/_cmagent IsHidden 1", commands);
        Assert.Contains(". -create /Users/_cmagent Password *", commands);
        Assert.Contains(". -create /Users/_cmagent UserShell /usr/bin/false", commands);
        Assert.Contains($". -create /Users/_cmagent NFSHomeDirectory {Mac.Home}", commands);
        Assert.Equal([".", "-read", "/Users/_cmagent"], LaunchdDaemon.AccountExistsArgs(Mac));
        Assert.Equal([[".", "-delete", "/Users/_cmagent"], [".", "-delete", "/Groups/_cmagent"]], LaunchdDaemon.AccountDeleteCommands(Mac));
    }

    // ---- Windows ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_service_is_created_under_the_virtual_account_with_a_quoted_binary_path_and_a_delayed_start()
    {
        Assert.Equal(["create", "cm-agent", "binPath=", "\"C:\\Program Files\\ClaudeMonitor\\cm-agent.exe\" daemon", "obj=", @"NT SERVICE\cm-agent", "start=", "delayed-auto"],
            WindowsServiceSetup.Create(Windows));
        Assert.Equal(["failure", "cm-agent", "reset=", "86400", "actions=", "restart/10000/restart/60000//"], WindowsServiceSetup.Failure());
        Assert.Equal(["sidtype", "cm-agent", "unrestricted"], WindowsServiceSetup.SidType());
    }

    [Fact]
    public void The_home_acl_gives_the_service_and_system_full_control_and_administrators_read_only_with_nothing_inherited()
    {
        var acl = WindowsServiceSetup.HomeAcl(Windows);
        Assert.Equal([@"C:\ProgramData\ClaudeMonitor\service", "/inheritance:r", "/grant:r", @"NT SERVICE\cm-agent:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)R"], acl);
        Assert.DoesNotContain(acl, a => a.StartsWith("*S-1-5-32-544", StringComparison.Ordinal) && a.EndsWith('F'));
        Assert.Equal([@"C:\Program Files\ClaudeMonitor", "/grant", @"NT SERVICE\cm-agent:(OI)(CI)RX"], WindowsServiceSetup.BinaryAcl(Windows));
    }

    [Fact]
    public void The_service_environment_is_one_multi_string_registry_value_under_the_service_key()
    {
        var args = WindowsServiceSetup.Environment(Windows with { Server = "https://monitor.invalid" });
        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Services\cm-agent", args[1]);
        Assert.Equal(["/v", "Environment", "/t", "REG_MULTI_SZ", "/s", "|"], args.Skip(2).Take(6));
        Assert.Equal($@"CM_SERVICE=1|CM_AGENT_HOME={Windows.Home}|DOTNET_BUNDLE_EXTRACT_BASE_DIR={Windows.Home}\.net|CM_SERVER=https://monitor.invalid", args[args.ToList().IndexOf("/d") + 1]);
        Assert.Equal("/f", args[^1]);
    }

    [Theory]
    [InlineData(@"\\server\share\cm-agent.exe")]
    [InlineData(@"relative\cm-agent.exe")]
    [InlineData("C:\\Program Files\\\"x\"\\cm-agent.exe")]
    [InlineData(@"C:\Program Files\..\Windows\cm-agent.exe")]
    [InlineData(@"C:\a|b\cm-agent.exe")]
    [InlineData("C:\\a\nb\\cm-agent.exe")]
    public void A_path_the_tools_would_read_as_something_else_is_refused(string binary)
    {
        Assert.Null(WindowsServiceSetup.Check(Windows));
        Assert.NotNull(WindowsServiceSetup.Check(Windows with { Binary = binary }));
        Assert.NotNull(WindowsServiceSetup.Check(Windows with { Home = binary }));
    }

    // ---- layout -----------------------------------------------------------------------------------------------

    [Fact]
    public void Each_os_keeps_the_binary_where_only_an_admin_can_write_and_never_in_a_users_home()
    {
        Assert.Equal(("/usr/local/lib/cm-agent/cm-agent", "/var/lib/cm-agent", "cm-agent", "/etc/cm-agent/exec.json"), (Linux.Binary, Linux.Home, Linux.Account, Linux.ExecConfigPath));
        Assert.Equal(("/Library/Application Support/ClaudeMonitor/bin/cm-agent", "_cmagent", "/Library/Application Support/ClaudeMonitor/exec.json"), (Mac.Binary, Mac.Account, Mac.ExecConfigPath));
        Assert.Equal((@"C:\Program Files\ClaudeMonitor\cm-agent.exe", @"NT SERVICE\cm-agent", @"C:\ProgramData\ClaudeMonitor\service"), (Windows.Binary, Windows.Account, Windows.Home));
        Assert.DoesNotContain("/Users/", Mac.Binary, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceLayout.For("plan9", "", ""));
        Assert.Equal(@"C:\Program Files\ClaudeMonitor", ServiceLayout.For(OsKinds.Windows, "", "").BinaryDir); // an unreadable folder falls back to the default
    }

    [Fact]
    public void The_server_comes_last_in_the_service_environment_and_the_login_code_sits_in_the_home()
    {
        Assert.Equal(["CM_SERVICE", "CM_AGENT_HOME", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"], Linux.ServiceEnvironment.Select(e => e.Name));
        Assert.Equal("CM_SERVER", (Linux with { Server = "https://m.invalid" }).ServiceEnvironment[^1].Name);
        Assert.Equal("/var/lib/cm-agent/login-code.txt", Linux.LoginCodePath);
        Assert.Equal(@"C:\ProgramData\ClaudeMonitor\service\login-code.txt", Windows.LoginCodePath);
    }
}
