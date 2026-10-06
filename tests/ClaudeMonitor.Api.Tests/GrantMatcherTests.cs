using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// What a grant template may be (ADR-0005, "Grants: full templates"): Validate refuses everything a template may never
/// say. Pure; the target's OS is a parameter, so every OS is tested from any machine.
/// </summary>
public sealed class GrantMatcherTests
{
    private const string Linux = OsKinds.Linux;
    private const string Mac = OsKinds.MacOs;
    private const string Win = OsKinds.Windows;
    private const string Dir = "/var/log/app";

    private static GrantTemplate Template(string[] argv, string cwd = Dir, int timeout = 60) => new(argv, cwd, timeout);

    private static string[] Tail(params string[] rest) => ["/usr/bin/tail", .. rest];

    [Fact]
    public void A_fixed_length_template_of_literals_and_placeholders_is_valid_on_every_os()
    {
        var unix = Template(Tail("-n", "{int:1..5000}", "--level={enum:info|warn}", "{word}", "{path:/var/log/app/}"));
        Assert.Null(GrantMatcher.Validate(unix, Linux, requestedByClaude: false));
        Assert.Null(GrantMatcher.Validate(unix, Mac, requestedByClaude: true));
        var windows = Template(["C:\\Program Files\\Tool\\tool.exe", "{int:1..9}", "{path:C:\\logs\\app\\}"], "C:\\logs\\app");
        Assert.Null(GrantMatcher.Validate(windows, Win, requestedByClaude: true));
    }

    [Fact]
    public void An_unknown_os_or_a_missing_part_is_refused_before_anything_else()
    {
        var ok = Template(Tail("-n"));
        Assert.Equal(GrantErrors.UnknownOs, GrantMatcher.Validate(ok, "freebsd", false));
        Assert.Equal(GrantErrors.UnknownOs, GrantMatcher.Validate(ok, "Linux", false));
        Assert.Equal(GrantErrors.UnknownOs, GrantMatcher.Validate(ok, "", false));
        Assert.Equal(GrantErrors.Malformed, GrantMatcher.Validate(null!, Linux, false));
        Assert.Equal(GrantErrors.Malformed, GrantMatcher.Validate(new GrantTemplate(null!, Dir, 60), Linux, false));
        Assert.Equal(GrantErrors.Malformed, GrantMatcher.Validate(new GrantTemplate(Tail(), null!, 60), Linux, false));
        Assert.Equal(GrantErrors.Malformed, GrantMatcher.Validate(Template([]), Linux, false));
        var tooLong = Template(["/usr/bin/tail", .. Enumerable.Repeat("x", 64)]);
        Assert.Equal(GrantErrors.Malformed, GrantMatcher.Validate(tooLong, Linux, false));
        var longest = Template(["/usr/bin/tail", .. Enumerable.Repeat("x", 63)]);
        Assert.Null(GrantMatcher.Validate(longest, Linux, false));
    }

    [Theory]
    [InlineData(Linux, "/bin/sh")]
    [InlineData(Linux, "/bin/bash")]
    [InlineData(Linux, "/usr/bin/env")]
    [InlineData(Linux, "/usr/bin/sudo")]
    [InlineData(Linux, "/usr/bin/python3")]
    [InlineData(Linux, "/usr/bin/python3.12")]
    [InlineData(Linux, "/usr/local/bin/pypy3.10")]
    [InlineData(Linux, "/usr/bin/perl5.30")]
    [InlineData(Linux, "/usr/bin/bash-5.2")]
    [InlineData(Linux, "/usr/bin/node")]
    [InlineData(Linux, "/usr/bin/ssh")]
    [InlineData(Linux, "/usr/bin/xargs")]
    [InlineData(Linux, "/usr/bin/awk")]
    [InlineData(Linux, "/usr/bin/vim")]
    [InlineData(Linux, "/usr/bin/less")]
    [InlineData(Linux, "/usr/bin/git")]
    [InlineData(Linux, "/usr/bin/docker")]
    [InlineData(Linux, "/usr/bin/dotnet")]
    [InlineData(Linux, "/BIN/SH")]
    [InlineData(Mac, "/usr/bin/osascript")]
    [InlineData(Mac, "/usr/bin/open")]
    [InlineData(Mac, "/bin/launchctl")]
    [InlineData(Win, "C:\\Windows\\System32\\cmd.exe")]
    [InlineData(Win, "C:\\Windows\\System32\\CMD.EXE")]
    [InlineData(Win, "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    [InlineData(Win, "C:\\Program Files\\PowerShell\\7\\pwsh.exe")]
    [InlineData(Win, "C:\\Windows\\System32\\wsl.exe")]
    [InlineData(Win, "C:\\Windows\\System32\\mshta.exe")]
    [InlineData(Win, "C:\\Windows\\System32\\rundll32.exe")]
    [InlineData(Win, "C:\\Windows\\System32\\regsvr32.exe")]
    [InlineData(Win, "C:\\Windows\\SysWOW64\\RUNDLL32.EXE")]
    [InlineData(Win, "C:\\Windows\\System32\\certutil.exe")]
    public void A_shell_launcher_or_interpreter_is_never_grantable(string os, string argv0)
    {
        Assert.Equal(GrantErrors.NeverGrantable, GrantMatcher.Validate(Template([argv0, "x"], os == Win ? "C:\\logs" : Dir), os, false));
        Assert.True(GrantMatcher.IsInterpreter(argv0, os));
    }

    [Theory]
    [InlineData(Linux, "tail")]
    [InlineData(Linux, "./tail")]
    [InlineData(Linux, "bin/tail")]
    [InlineData(Linux, "/usr/bin/../bin/tail")]
    [InlineData(Linux, "/usr/bin/./tail")]
    [InlineData(Linux, "/usr//bin/tail")]
    [InlineData(Linux, "/usr/bin/ta$il")]
    [InlineData(Linux, "/usr/bin/tail ")]
    [InlineData(Linux, "/usr/bin/tä il")]
    [InlineData(Linux, "/")]
    [InlineData(Linux, "")]
    [InlineData(Linux, "C:\\tools\\tail.exe")]
    [InlineData(Win, "/usr/bin/tail.exe")]
    [InlineData(Win, "tail.exe")]
    [InlineData(Win, "C:\\tools\\run.bat")]
    [InlineData(Win, "C:\\tools\\RUN.CMD")]
    [InlineData(Win, "C:\\tools\\tool.com")]
    [InlineData(Win, "C:\\tools\\.exe")]
    [InlineData(Win, "C:tools\\tool.exe")]
    [InlineData(Win, "\\\\server\\share\\tool.exe")]
    [InlineData(Win, "\\\\?\\C:\\tools\\tool.exe")]
    [InlineData(Win, "C:\\tools\\tool.exe::$DATA")]
    [InlineData(Win, "C:\\tools\\tool.exe:stream")]
    [InlineData(Win, "C:\\tools\\CON.exe")]
    [InlineData(Win, "C:\\tools\\nul.exe")]
    [InlineData(Win, "C:\\tools\\COM1.exe")]
    [InlineData(Win, "C:\\tools\\dir.\\tool.exe")]
    [InlineData(Win, "C:\\tools\\..\\tool.exe")]
    [InlineData(Win, "C:\\tools\\tool.exe.")]
    [InlineData(Win, "C:/tools/tool.exe")]
    public void The_program_must_be_an_absolute_clean_path_and_on_windows_an_exe(string os, string argv0)
    {
        var cwd = os == Win ? "C:\\logs" : Dir;
        Assert.Equal(GrantErrors.Argv0, GrantMatcher.Validate(Template([argv0, "x"], cwd), os, false));
        Assert.False(GrantMatcher.IsAbsoluteExecutable(argv0, os));
    }

    [Fact]
    public void A_program_path_may_hold_a_space_and_an_absolute_exe_passes()
    {
        Assert.True(GrantMatcher.IsAbsoluteExecutable("/opt/my tools/tail", Linux));
        Assert.True(GrantMatcher.IsAbsoluteExecutable("C:\\Program Files\\Tool\\TOOL.EXE", Win));
        Assert.False(GrantMatcher.IsAbsoluteExecutable("/usr/bin/tail", "freebsd"));
        Assert.False(GrantMatcher.IsAbsoluteExecutable(null, Linux));
    }

    [Theory]
    [InlineData("{int:0..}")]
    [InlineData("{int:5..1}")]
    [InlineData("{int:05..9}")]
    [InlineData("{int:-1..9}")]
    [InlineData("{int:1..9999999999}")]
    [InlineData("{int:a..b}")]
    [InlineData("{int:1}")]
    [InlineData("{word")]
    [InlineData("word}")]
    [InlineData("{word}{word}")]
    [InlineData("{word}x")]
    [InlineData("x{word}")]
    [InlineData("-n{word}")]
    [InlineData("={word}")]
    [InlineData("{bogus}")]
    [InlineData("{}")]
    [InlineData("{path:}")]
    [InlineData("{enum:a|b c}")]
    [InlineData("{enum:a||b}")]
    [InlineData("{enum:}")]
    [InlineData("{enum:a/b}")]
    [InlineData("--x={")]
    public void A_malformed_placeholder_is_refused(string element) =>
        Assert.Equal(GrantErrors.Placeholder, GrantMatcher.Validate(Template(Tail(element)), Linux, false));

    [Theory]
    [InlineData("")]
    [InlineData("ä")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\u202eb")]
    public void A_literal_must_be_printable_ascii_and_not_empty(string element) =>
        Assert.Equal(GrantErrors.Element, GrantMatcher.Validate(Template(Tail(element)), Linux, false));

    [Fact]
    public void A_literal_longer_than_1024_characters_is_refused_and_one_of_1024_passes()
    {
        Assert.Equal(GrantErrors.Element, GrantMatcher.Validate(Template(Tail(new string('a', 1025))), Linux, false));
        Assert.Null(GrantMatcher.Validate(Template(Tail(new string('a', 1024))), Linux, false));
    }

    [Fact]
    public void A_double_quote_is_refused_in_a_windows_literal_only()
    {
        var withQuote = Template(["C:\\tools\\tool.exe", "a\"b"], "C:\\logs");
        Assert.Equal(GrantErrors.Element, GrantMatcher.Validate(withQuote, Win, false));
        Assert.Null(GrantMatcher.Validate(Template(Tail("a\"b")), Linux, false));
    }

    [Theory]
    [InlineData(Linux, "{path:/}")]
    [InlineData(Linux, "{path:/var}")]
    [InlineData(Linux, "{path:/var/}")]
    [InlineData(Linux, "{path:/home}")]
    [InlineData(Linux, "{path:/etc}")]
    [InlineData(Linux, "{path:/etc/nginx}")]
    [InlineData(Mac, "{path:/ETC/nginx}")]
    [InlineData(Linux, "{path:/root/x}")]
    [InlineData(Linux, "{path:/proc/1}")]
    [InlineData(Linux, "{path:/sys/class}")]
    [InlineData(Linux, "{path:/dev/shm}")]
    [InlineData(Mac, "{path:/private/etc/ssh}")]
    [InlineData(Mac, "{path:/Etc/ssh}")]
    [InlineData(Linux, "{path:var/log}")]
    [InlineData(Linux, "{path:/var/../log/x}")]
    [InlineData(Linux, "{path:/var/./log}")]
    [InlineData(Linux, "{path:/var//log}")]
    [InlineData(Linux, "{path:/var/log app/x}")]
    [InlineData(Linux, "{path:/var/lo'g}")]
    [InlineData(Linux, "{path:/var/log/$x}")]
    [InlineData(Linux, "{path:/var/lög}")]
    [InlineData(Win, "{path:C:\\}")]
    [InlineData(Win, "{path:C:\\logs}")]
    [InlineData(Win, "{path:C:\\Windows\\Logs}")]
    [InlineData(Win, "{path:c:\\WINDOWS\\Logs}")]
    [InlineData(Win, "{path:C:\\Users}")]
    [InlineData(Win, "{path:\\\\server\\share\\x}")]
    [InlineData(Win, "{path:C:\\logs\\..\\x}")]
    [InlineData(Win, "{path:C:\\logs\\a:b}")]
    [InlineData(Win, "{path:C:\\logs\\CON}")]
    [InlineData(Win, "{path:C:\\logs\\x.}")]
    [InlineData(Win, "{path:C:/logs/x}")]
    public void A_path_root_that_is_shallow_forbidden_or_unclean_is_refused(string os, string element)
    {
        var argv = os == Win ? new[] { "C:\\tools\\tool.exe", element } : Tail(element);
        Assert.Equal(GrantErrors.Root, GrantMatcher.Validate(Template(argv, os == Win ? "C:\\logs\\app" : Dir), os, false));
    }

    [Theory]
    [InlineData(Linux, "/", true)]
    [InlineData(Linux, "/var", true)]
    [InlineData(Linux, "/etc/x", true)]
    [InlineData(Linux, "/ETC/x", false)]
    [InlineData(Mac, "/ETC/x", true)]
    [InlineData(Linux, "/var/log", false)]
    [InlineData(Mac, "/private/etc/x", true)]
    [InlineData(Mac, "/private/var/log", false)]
    [InlineData(Win, "C:\\", true)]
    [InlineData(Win, "C:\\Windows\\x", true)]
    [InlineData(Win, "C:\\logs\\app", false)]
    [InlineData("freebsd", "/var/log", true)]
    public void A_forbidden_root_is_judged_by_depth_and_by_the_protected_folders(string os, string root, bool forbidden) =>
        Assert.Equal(forbidden, GrantMatcher.IsForbiddenRoot(root, os));

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    [InlineData("/var/../x")]
    [InlineData("/var//x")]
    [InlineData("/var/log/{x}")]
    [InlineData("/var/lo\"g")]
    [InlineData("/var/log/")]
    public void The_working_directory_is_a_clean_absolute_literal(string cwd) =>
        Assert.Equal(GrantErrors.Cwd, GrantMatcher.Validate(Template(Tail("-n"), cwd), Linux, false));

    [Fact]
    public void The_working_directory_may_hold_a_space_and_a_windows_one_needs_a_drive()
    {
        Assert.Null(GrantMatcher.Validate(Template(Tail("-n"), "/srv/my app"), Linux, false));
        Assert.Equal(GrantErrors.Cwd, GrantMatcher.Validate(Template(["C:\\t\\t.exe"], "/srv/app"), Win, false));
    }

    [Theory]
    [InlineData(0, GrantErrors.Timeout)]
    [InlineData(-1, GrantErrors.Timeout)]
    [InlineData(3601, GrantErrors.Timeout)]
    [InlineData(int.MaxValue, GrantErrors.Timeout)]
    public void A_longest_timeout_outside_one_to_3600_seconds_is_refused(int seconds, string error) =>
        Assert.Equal(error, GrantMatcher.Validate(Template(Tail("-n"), Dir, seconds), Linux, false));

    [Fact]
    public void The_shortest_and_longest_allowed_timeouts_are_valid()
    {
        Assert.Null(GrantMatcher.Validate(Template(Tail("-n"), Dir, 1), Linux, false));
        Assert.Null(GrantMatcher.Validate(Template(Tail("-n"), Dir, 3600), Linux, false));
    }
}
