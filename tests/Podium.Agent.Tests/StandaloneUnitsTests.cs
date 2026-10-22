using System.Buffers.Binary;
using System.Xml;
using Podium.Agent.Install;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The files of a standalone agent's autostart (ADR-0008 §3g): rendered from the layout alone, well-formed, in the user's own folders, and refusing what would need quoting.</summary>
public sealed class StandaloneUnitsTests
{
    private static StandaloneLayout Mac() => StandaloneLayout.For(OsKinds.MacOs, "/Users/ayse", "/Users/ayse/.aipod", "ayse");
    private static StandaloneLayout Linux() => StandaloneLayout.For(OsKinds.Linux, "/home/ayse", "/home/ayse/.aipod", "ayse");
    private static StandaloneLayout Windows() => StandaloneLayout.For(OsKinds.Windows, @"C:\Users\ayse", @"C:\Users\ayse\.aipod", "ayse");

    [Fact]
    public void Everything_lives_in_the_users_own_home_and_account()
    {
        Assert.Equal(("/Users/ayse/.aipod/bin/aipod", "/Users/ayse/.aipod/Aipod.app/Contents/MacOS/aipod", "/Users/ayse/Library/LaunchAgents/aipod.agent.user.plist"),
            (Mac().Binary, Mac().AutostartBinary, Mac().DefinitionPath));
        Assert.Equal(("/home/ayse/.aipod/bin/aipod", "/home/ayse/.config/systemd/user/aipod.service"), (Linux().AutostartBinary, Linux().DefinitionPath));
        Assert.Equal((@"C:\Users\ayse\.aipod\bin\aipod.exe", @"C:\Users\ayse\.aipod\bin\aipodw.exe"), (Windows().Binary, Windows().AutostartBinary));
        Assert.Null(Linux().BundlePath);
        Assert.Throws<ArgumentOutOfRangeException>(() => StandaloneLayout.For("plan9", "/x", "/x/h", "u"));
    }

    [Fact]
    public void The_launch_agent_starts_the_app_bundles_executable_at_login_and_restarts_only_after_a_failure()
    {
        var xml = StandaloneUnits.RenderLaunchAgent(Mac());
        var doc = System.Xml.Linq.XDocument.Parse(xml[(xml.IndexOf("<plist", StringComparison.Ordinal))..]);
        Assert.NotNull(doc.Root);
        Assert.Contains("<key>Label</key><string>aipod.agent.user</string>", xml, StringComparison.Ordinal);
        Assert.Contains("<string>/Users/ayse/.aipod/Aipod.app/Contents/MacOS/aipod</string><string>daemon</string>", xml, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key><true/>", xml, StringComparison.Ordinal);
        Assert.Contains("<dict><key>SuccessfulExit</key><false/></dict>", xml, StringComparison.Ordinal);
        Assert.Contains("<key>AP_STANDALONE</key><string>1</string>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("UserName", xml, StringComparison.Ordinal); // it runs as the user who logs in
    }

    [Fact]
    public void A_path_with_markup_characters_is_escaped_in_the_plist_and_a_relative_one_is_refused()
    {
        var odd = Mac() with { AutostartBinary = "/Users/a&b/<x>/aipod" };
        var xml = StandaloneUnits.RenderLaunchAgent(odd);
        Assert.Contains("/Users/a&amp;b/&lt;x&gt;/aipod", xml, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => StandaloneUnits.RenderLaunchAgent(Mac() with { AutostartBinary = "relative/aipod" }));
    }

    [Fact]
    public void The_bundle_is_an_agent_application_with_no_dock_icon_and_no_menu()
    {
        var plist = StandaloneUnits.RenderInfoPlist("0.3.0");
        Assert.Contains("<key>CFBundleExecutable</key><string>aipod</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>LSUIElement</key><true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleShortVersionString</key><string>0.3.0</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void The_systemd_user_unit_restarts_on_failure_ten_seconds_apart_with_a_widened_start_limit_and_starts_with_the_session()
    {
        var unit = StandaloneUnits.RenderSystemd(Linux());
        Assert.Contains("ExecStart=/home/ayse/.aipod/bin/aipod daemon", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("RestartSec=10", unit, StringComparison.Ordinal);
        Assert.Contains("StartLimitBurst=20", unit, StringComparison.Ordinal);
        Assert.Contains("Environment=AP_STANDALONE=1", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("User=", unit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/home/ay se/.aipod")]
    [InlineData("/home/ayse/.aipod/%h")]
    [InlineData("/home/ayse/../root")]
    [InlineData("relative/home")]
    public void A_home_that_would_need_quoting_or_is_not_plain_is_refused_for_the_unit(string home)
    {
        var l = StandaloneLayout.For(OsKinds.Linux, "/home/ayse", home, "ayse");
        l = l with { AutostartBinary = home + "/bin/aipod" };
        Assert.NotNull(StandaloneUnits.CheckSystemd(l));
        Assert.Throws<ArgumentException>(() => StandaloneUnits.RenderSystemd(l));
    }

    [Fact]
    public void The_logon_task_runs_the_windowless_build_at_this_users_logon_with_least_privilege_and_restarts_on_failure()
    {
        var xml = StandaloneUnits.RenderLogonTask(Windows());
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(xml[(xml.IndexOf("<Task", StringComparison.Ordinal))..]).Root);
        Assert.Contains(@"<Command>C:\Users\ayse\.aipod\bin\aipodw.exe</Command><Arguments>daemon</Arguments>", xml, StringComparison.Ordinal);
        Assert.Contains("<LogonTrigger>", xml, StringComparison.Ordinal);
        Assert.Contains("<UserId>ayse</UserId>", xml, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<RestartOnFailure>", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => StandaloneUnits.RenderLogonTask(Windows() with { AutostartBinary = "aipodw.exe" }));
    }

    [Fact]
    public void A_user_name_with_xml_characters_is_escaped_in_the_task_and_a_control_character_is_refused()
    {
        Assert.Contains("<UserId>a&amp;b</UserId>", StandaloneUnits.RenderLogonTask(Windows() with { User = "a&b" }), StringComparison.Ordinal);
        Assert.Throws<XmlException>(() => StandaloneUnits.RenderLogonTask(Windows() with { User = "a\u0001b" }));
    }

    // ---- the PE header: the console build as a windowless one --------------------------------------------------------

    private static byte[] Pe(ushort subsystem)
    {
        var file = new byte[1024];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x3C), 0x80);
        file[0x80] = (byte)'P';
        file[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x80 + 24 + 68), subsystem);
        return file;
    }

    [Fact]
    public void A_console_pe_header_becomes_a_gui_one_in_that_one_field_and_nothing_else_changes()
    {
        var file = Pe(PeSubsystem.Console);
        var before = (byte[])file.Clone();

        Assert.True(PeSubsystem.SetGui(file));

        Assert.Equal(PeSubsystem.Gui, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x80 + 24 + 68)));
        Assert.Equal(1, file.Zip(before).Count(p => p.First != p.Second)); // 3 -> 2 changes one byte
    }

    [Fact]
    public void A_gui_program_a_file_that_is_not_pe_and_a_short_file_are_left_alone()
    {
        var gui = Pe(PeSubsystem.Gui);
        Assert.False(PeSubsystem.SetGui(gui));
        var notPe = new byte[1024];
        Assert.False(PeSubsystem.SetGui(notPe));
        Assert.False(PeSubsystem.SetGui(new byte[10]));
        var badOffset = Pe(PeSubsystem.Console);
        BinaryPrimitives.WriteInt32LittleEndian(badOffset.AsSpan(0x3C), 5000);
        Assert.False(PeSubsystem.SetGui(badOffset));
    }

    [Fact]
    public void The_file_version_patches_in_place_and_leaves_a_non_pe_file_untouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ap-pe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "a.exe");
            File.WriteAllBytes(exe, Pe(PeSubsystem.Console));
            Assert.True(PeSubsystem.TrySetGui(exe));
            Assert.Equal(PeSubsystem.Gui, BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(exe).AsSpan(0x80 + 24 + 68)));
            Assert.False(PeSubsystem.TrySetGui(exe)); // already GUI

            var text = Path.Combine(dir, "b.txt");
            File.WriteAllText(text, "not an executable");
            Assert.False(PeSubsystem.TrySetGui(text));
            Assert.Equal("not an executable", File.ReadAllText(text));
            Assert.False(PeSubsystem.TrySetGui(Path.Combine(dir, "missing.exe")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
