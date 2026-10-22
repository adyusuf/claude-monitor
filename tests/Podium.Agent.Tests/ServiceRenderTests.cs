using System.Xml.Linq;
using Podium.Agent.Install;
using Podium.Contracts;

namespace Podium.Agent.Tests;

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
    [InlineData("ReadWritePaths=/var/lib/aipod")]
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
    [InlineData("Environment=AP_SERVICE=1")]
    [InlineData("Environment=AP_AGENT_HOME=/var/lib/aipod")]
    [InlineData("Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/aipod/.net")]
    [InlineData("User=aipod")]
    [InlineData("Group=aipod")]
    [InlineData("ExecStart=/usr/local/lib/aipod/aipod daemon")]
    [InlineData("MemoryMax=1G")]
    [InlineData("TasksMax=256")]
    [InlineData("CPUQuota=50%")]
    [InlineData("WantedBy=multi-user.target")]
    public void The_systemd_unit_carries_every_hardening_and_service_directive(string line) =>
        Assert.Contains(line, Lines(SystemdUnit.Render(Linux)));

    [Fact]
    public void The_server_is_in_the_unit_only_when_one_is_set()
    {
        Assert.DoesNotContain("AP_SERVER", SystemdUnit.Render(Linux), StringComparison.Ordinal);
        Assert.Contains("Environment=AP_SERVER=https://aipod.invalid", Lines(SystemdUnit.Render(Linux with { Server = "https://aipod.invalid" })));
    }

    [Theory]
    [InlineData("/var/lib/ap agent")]
    [InlineData("/var/lib/aipod\nExecStartPost=/bin/evil")]
    [InlineData("/var/lib/aipod\r")]
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
    [InlineData("ap agent")]
    [InlineData("aipod\nUser=root")]
    [InlineData("")]
    public void An_account_name_that_is_not_a_plain_system_name_is_refused(string account) =>
        Assert.NotNull(SystemdUnit.Check(Linux with { Account = account }));

    [Fact]
    public void The_account_is_created_locked_with_its_own_group_and_no_login_shell()
    {
        Assert.Equal(["--system", "--user-group", "--home-dir", "/var/lib/aipod", "--no-create-home", "--shell", "/usr/sbin/nologin", "aipod"], SystemdUnit.UserAddArgs(Linux));
        Assert.Null(SystemdUnit.Check(Linux));
    }

    // ---- launchd ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_launch_daemon_runs_as_the_role_account_with_umask_077_and_the_service_environment()
    {
        var doc = XDocument.Parse(LaunchdDaemon.Render(Mac with { Server = "https://aipod.invalid" }).Replace("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">", "", StringComparison.Ordinal));
        var dict = doc.Root!.Element("dict")!;
        var values = dict.Elements().Chunk(2).ToDictionary(p => p[0].Value, p => p[1]);

        Assert.Equal("_cmagent", values["UserName"].Value);
        Assert.Equal("_cmagent", values["GroupName"].Value);
        Assert.Equal("63", values["Umask"].Value);
        Assert.Equal("com.aipod.agent", values["Label"].Value);
        Assert.Equal([Mac.Binary, "daemon"], values["ProgramArguments"].Elements("string").Select(e => e.Value));
        Assert.Equal("true", values["RunAtLoad"].Name.LocalName);
        Assert.Equal(["SuccessfulExit", "false"], values["KeepAlive"].Elements().Select(e => e.Name.LocalName == "key" ? e.Value : e.Name.LocalName)); // restart only after a failure
        var env = values["EnvironmentVariables"].Elements().Chunk(2).ToDictionary(p => p[0].Value, p => p[1].Value);
        Assert.Equal(("1", Mac.Home, "https://aipod.invalid"), (env["AP_SERVICE"], env["AP_AGENT_HOME"], env["AP_SERVER"]));
    }

    [Fact]
    public void Text_that_is_xml_is_escaped_and_a_character_xml_cannot_carry_is_refused()
    {
        var text = LaunchdDaemon.Render(Mac with { Server = "https://x.invalid/?a=1&b=<2>" });
        Assert.Contains("https://x.invalid/?a=1&amp;b=&lt;2&gt;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<2>", text, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => LaunchdDaemon.Render(Mac with { Server = "https://x.invalid/\u0001" }));
        Assert.Throws<ArgumentException>(() => LaunchdDaemon.Render(Mac with { Binary = "relative/aipod" }));
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
        Assert.Equal(["create", "aipod", "binPath=", "\"C:\\Program Files\\Podium\\aipod.exe\" daemon", "obj=", @"NT SERVICE\aipod", "start=", "delayed-auto", "DisplayName=", "aipod agent"],
            WindowsServiceSetup.Create(Windows));
        Assert.Equal(["failure", "aipod", "reset=", "86400", "actions=", "restart/10000/restart/60000//"], WindowsServiceSetup.Failure());
        Assert.Equal(["sidtype", "aipod", "unrestricted"], WindowsServiceSetup.SidType());
    }

    [Fact]
    public void The_home_acl_gives_the_service_and_system_full_control_and_administrators_read_only_with_nothing_inherited()
    {
        var acl = WindowsServiceSetup.HomeAcl(Windows);
        Assert.Equal([@"C:\ProgramData\Podium\service", "/inheritance:r", "/grant:r", @"NT SERVICE\aipod:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)R"], acl);
        Assert.DoesNotContain(acl, a => a.StartsWith("*S-1-5-32-544", StringComparison.Ordinal) && a.EndsWith('F'));
        Assert.Equal([@"C:\Program Files\Podium", "/grant", @"NT SERVICE\aipod:(OI)(CI)RX"], WindowsServiceSetup.BinaryAcl(Windows));
    }

    [Fact]
    public void The_service_environment_is_one_multi_string_registry_value_under_the_service_key()
    {
        var args = WindowsServiceSetup.Environment(Windows with { Server = "https://aipod.invalid" });
        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Services\aipod", args[1]);
        Assert.Equal(["/v", "Environment", "/t", "REG_MULTI_SZ", "/s", "|"], args.Skip(2).Take(6));
        Assert.Equal($@"AP_SERVICE=1|AP_AGENT_HOME={Windows.Home}|DOTNET_BUNDLE_EXTRACT_BASE_DIR={Windows.Home}\.net|AP_SERVER=https://aipod.invalid", args[args.ToList().IndexOf("/d") + 1]);
        Assert.Equal("/f", args[^1]);
    }

    [Theory]
    [InlineData(@"\\server\share\aipod.exe")]
    [InlineData(@"relative\aipod.exe")]
    [InlineData("C:\\Program Files\\\"x\"\\aipod.exe")]
    [InlineData(@"C:\Program Files\..\Windows\aipod.exe")]
    [InlineData(@"C:\a|b\aipod.exe")]
    [InlineData("C:\\a\nb\\aipod.exe")]
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
        Assert.Equal(("/usr/local/lib/aipod/aipod", "/var/lib/aipod", "aipod", "/etc/aipod/exec.json"), (Linux.Binary, Linux.Home, Linux.Account, Linux.ExecConfigPath));
        Assert.Equal(("/Library/Application Support/Podium/bin/aipod", "_cmagent", "/Library/Application Support/Podium/exec.json"), (Mac.Binary, Mac.Account, Mac.ExecConfigPath));
        Assert.Equal((@"C:\Program Files\Podium\aipod.exe", @"NT SERVICE\aipod", @"C:\ProgramData\Podium\service"), (Windows.Binary, Windows.Account, Windows.Home));
        Assert.DoesNotContain("/Users/", Mac.Binary, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceLayout.For("plan9", "", ""));
        Assert.Equal(@"C:\Program Files\Podium", ServiceLayout.For(OsKinds.Windows, "", "").BinaryDir); // an unreadable folder falls back to the default
    }

    [Fact]
    public void The_server_comes_last_in_the_service_environment_and_the_login_code_sits_in_the_home()
    {
        Assert.Equal(["AP_SERVICE", "AP_AGENT_HOME", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"], Linux.ServiceEnvironment.Select(e => e.Name));
        Assert.Equal("AP_SERVER", (Linux with { Server = "https://m.invalid" }).ServiceEnvironment[^1].Name);
        Assert.Equal("/var/lib/aipod/login-code.txt", Linux.LoginCodePath);
        Assert.Equal(@"C:\ProgramData\Podium\service\login-code.txt", Windows.LoginCodePath);
    }
}
