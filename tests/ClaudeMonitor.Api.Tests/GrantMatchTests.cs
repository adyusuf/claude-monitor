using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// The lexical match of one call against a template (ADR-0005; the security review's P/F list). Pure: it never touches
/// the file system, the target repeats it with real paths before exec.
/// </summary>
public sealed class GrantMatchTests
{
    private const string Linux = OsKinds.Linux;
    private const string Mac = OsKinds.MacOs;
    private const string Win = OsKinds.Windows;
    private const string Cwd = "/var/log/app";

    private static readonly GrantTemplate Tail =
        new(["/usr/bin/tail", "-n", "{int:1..5000}", "{path:/var/log/app/}"], Cwd, 60);

    private static bool Hit(GrantTemplate grant, string os, string? cwd, int timeout, params string[] argv) =>
        GrantMatcher.Matches(grant, argv, cwd, timeout, os);

    private static bool Tails(string os, params string[] argv) => Hit(Tail, os, Cwd, 30, argv);

    private static bool Value(string element, string value, string os = Linux, string? program = null) =>
        Hit(new GrantTemplate([program ?? "/usr/local/bin/tool", element], Cwd, 60), os, Cwd, 30, program ?? "/usr/local/bin/tool", value);

    [Fact]
    public void A_call_that_fits_every_element_matches_and_names_its_path_value_and_root()
    {
        var call = new[] { "/usr/bin/tail", "-n", "200", "/var/log/app/api.log" };
        Assert.True(GrantMatcher.Matches(Tail, call, Cwd, 30, Linux));
        var use = Assert.Single(GrantMatcher.PathUses(Tail, call, Linux));
        Assert.Equal(new GrantPathUse("/var/log/app/api.log", "/var/log/app/"), use);
        Assert.Empty(GrantMatcher.PathUses(Tail, ["/usr/bin/tail", "-n", "0", "/var/log/app/api.log"], Linux));
        Assert.Empty(GrantMatcher.PathUses(Tail, ["/usr/bin/tail", "-n", "5", "/etc/shadow"], Linux));
    }

    [Theory]
    [InlineData("/usr/bin/tail")]
    [InlineData("/usr/bin/tail|-n|5")]
    [InlineData("/usr/bin/tail|-n|5|/var/log/app/a|extra")]
    [InlineData("/usr/bin/head|-n|5|/var/log/app/a")]
    [InlineData("/usr/bin/tail|-m|5|/var/log/app/a")]
    [InlineData("/usr/bin/tail|-N|5|/var/log/app/a")]
    [InlineData("|-n|5|/var/log/app/a")]
    public void A_different_length_or_a_different_literal_never_matches(string joined) =>
        Assert.False(Tails(Linux, joined.Split('|')));

    [Fact]
    public void An_empty_call_or_a_template_that_was_never_validated_matches_nothing()
    {
        Assert.False(GrantMatcher.Matches(Tail, [], Cwd, 30, Linux));
        Assert.False(GrantMatcher.Matches(Tail, ["/usr/bin/tail", "-n", "5", "/var/log/app/a"], Cwd, 30, "plan9"));
        var broken = new GrantTemplate(["/usr/bin/tool", "{int:}"], Cwd, 60);
        Assert.False(Hit(broken, Linux, Cwd, 30, "/usr/bin/tool", "5"));
        Assert.False(Hit(broken, Linux, Cwd, 30, "/usr/bin/tool", "{int:}"));
        Assert.False(Hit(new GrantTemplate(["/usr/bin/tool", "-ö"], Cwd, 60), Linux, Cwd, 30, "/usr/bin/tool", "-ö"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("200", true)]
    [InlineData("5000", true)]
    [InlineData("0", false)]
    [InlineData("5001", false)]
    [InlineData("05000", false)]
    [InlineData("-1", false)]
    [InlineData("+5", false)]
    [InlineData("1e3", false)]
    [InlineData("1.5", false)]
    [InlineData("", false)]
    [InlineData(" 5", false)]
    [InlineData("5 ", false)]
    [InlineData("٣", false)]
    [InlineData("1234567890", false)]
    public void An_int_is_ascii_digits_in_range_without_sign_or_leading_zero(string value, bool expected) =>
        Assert.Equal(expected, Tails(Linux, "/usr/bin/tail", "-n", value, "/var/log/app/a"));

    [Fact]
    public void An_int_may_be_zero_and_the_bounds_belong_to_the_range()
    {
        Assert.True(Value("{int:0..9}", "0"));
        Assert.True(Value("{int:0..9}", "9"));
        Assert.False(Value("{int:0..9}", "10"));
        Assert.False(Value("{int:0..9}", "00"));
        Assert.True(Value("{int:10..10}", "10"));
        Assert.False(Value("{int:10..10}", "9"));
    }

    [Theory]
    [InlineData("api.log", true)]
    [InlineData("a", true)]
    [InlineData("9", true)]
    [InlineData("v1.2:3@x-y_z", true)]
    [InlineData("-x", false)]
    [InlineData("+e", false)]
    [InlineData("--output=/tmp/x", false)]
    [InlineData("-exec", false)]
    [InlineData("a b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a'b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a=b", false)]
    [InlineData("a;b", false)]
    [InlineData("ä", false)]
    [InlineData("a\u202e", false)]
    [InlineData("a\0b", false)]
    [InlineData("a\nb", false)]
    [InlineData(".hidden", false)]
    [InlineData("", false)]
    public void A_word_starts_alphanumeric_and_stays_in_its_small_charset(string value, bool expected) =>
        Assert.Equal(expected, Value("{word}", value));

    [Fact]
    public void A_word_may_be_128_characters_but_not_129()
    {
        Assert.True(Value("{word}", new string('a', 128)));
        Assert.False(Value("{word}", new string('a', 129)));
    }

    [Theory]
    [InlineData("/var/log/app/api.log", true)]
    [InlineData("/var/log/app", true)]
    [InlineData("/var/log/app/a/b/c", true)]
    [InlineData("/var/log/app/-x", true)]
    [InlineData("../secret", false)]
    [InlineData("api.log", false)]
    [InlineData("/var/log/app/../secret", false)]
    [InlineData("/var/log/app/./x", false)]
    [InlineData("/var/log/app/..", false)]
    [InlineData("/var/log/app-x/y", false)]
    [InlineData("/var/log/ap", false)]
    [InlineData("/var/log", false)]
    [InlineData("/var/log/app//x", false)]
    [InlineData("/var/log/app/", false)]
    [InlineData("/var/log/app/a b", false)]
    [InlineData("/var/log/app/a'b", false)]
    [InlineData("/var/log/app/a\"b", false)]
    [InlineData("/var/log/app/a$b", false)]
    [InlineData("/var/log/app/a*", false)]
    [InlineData("/var/log/app/ä", false)]
    [InlineData("/var/log/app/a\u202e", false)]
    [InlineData("/var/log/app/a\u0001", false)]
    [InlineData("/var/log/app/a\0", false)]
    [InlineData("/VAR/log/app/x", false)]
    [InlineData("-n", false)]
    [InlineData("+/var/log/app/x", false)]
    [InlineData("/etc/shadow", false)]
    [InlineData("", false)]
    public void A_path_stays_clean_and_under_its_root_at_a_separator_boundary(string value, bool expected) =>
        Assert.Equal(expected, Tails(Linux, "/usr/bin/tail", "-n", "5", value));

    [Fact]
    public void A_path_root_without_a_trailing_separator_works_the_same()
    {
        var grant = new GrantTemplate(["/usr/bin/tail", "{path:/var/log/app}"], Cwd, 60);
        Assert.True(Hit(grant, Linux, Cwd, 30, "/usr/bin/tail", "/var/log/app/x"));
        Assert.True(Hit(grant, Linux, Cwd, 30, "/usr/bin/tail", "/var/log/app"));
        Assert.False(Hit(grant, Linux, Cwd, 30, "/usr/bin/tail", "/var/log/application"));
    }

    [Fact]
    public void A_path_template_with_an_unclean_root_matches_nothing_even_when_it_was_never_validated()
    {
        Assert.False(Value("{path:/var/../etc/}", "/var/../etc/x"));
        Assert.False(Value("{path:/var/log/ap pe/}", "/var/log/ap pe/x"));
    }

    [Fact]
    public void A_path_is_case_sensitive_on_linux_and_case_insensitive_on_macos_and_windows()
    {
        Assert.False(Tails(Linux, "/usr/bin/tail", "-n", "5", "/VAR/LOG/APP/x"));
        Assert.True(Tails(Mac, "/usr/bin/tail", "-n", "5", "/VAR/LOG/APP/x"));
        var win = new GrantTemplate(["C:\\Tools\\App.exe", "{path:C:\\logs\\app\\}"], "C:\\logs\\app", 60);
        Assert.True(Hit(win, Win, "C:\\logs\\app", 30, "C:\\Tools\\App.exe", "C:\\logs\\app\\x.log"));
        Assert.True(Hit(win, Win, "c:\\LOGS\\app", 30, "c:\\tools\\APP.EXE", "c:\\LOGS\\APP\\X.LOG"));
    }

    [Theory]
    [InlineData("C:\\logs\\app\\x.log", true)]
    [InlineData("C:\\logs\\app\\sub\\COM10", true)]
    [InlineData("C:\\logs\\app\\x.log::$DATA", false)]
    [InlineData("C:\\logs\\app\\x.log:stream", false)]
    [InlineData("C:\\logs\\app\\CON", false)]
    [InlineData("C:\\logs\\app\\con.txt", false)]
    [InlineData("C:\\logs\\app\\NUL", false)]
    [InlineData("C:\\logs\\app\\COM1", false)]
    [InlineData("C:\\logs\\app\\LPT9.log", false)]
    [InlineData("C:\\logs\\app\\x.", false)]
    [InlineData("C:\\logs\\app\\x ", false)]
    [InlineData("\\\\server\\share\\x", false)]
    [InlineData("\\\\?\\C:\\logs\\app\\x", false)]
    [InlineData("\\\\.\\C:\\logs\\app\\x", false)]
    [InlineData("C:\\logs\\app\\..\\x", false)]
    [InlineData("C:\\logs\\appx\\y", false)]
    [InlineData("C:\\logs\\app\\\\x", false)]
    [InlineData("C:/logs/app/x", false)]
    [InlineData("\\logs\\app\\x", false)]
    [InlineData("logs\\app\\x", false)]
    [InlineData("D:\\logs\\app\\x", false)]
    public void A_windows_path_refuses_streams_devices_unc_and_dot_segments(string value, bool expected)
    {
        var win = new GrantTemplate(["C:\\Tools\\App.exe", "{path:C:\\logs\\app\\}"], "C:\\logs\\app", 60);
        Assert.Equal(expected, Hit(win, Win, "C:\\logs\\app", 30, "C:\\Tools\\App.exe", value));
    }

    [Fact]
    public void An_enum_takes_one_of_its_choices_exactly()
    {
        Assert.True(Value("{enum:info|warn|a.b-c}", "info"));
        Assert.True(Value("{enum:info|warn|a.b-c}", "a.b-c"));
        Assert.False(Value("{enum:info|warn}", "Info"));
        Assert.False(Value("{enum:info|warn}", "info|warn"));
        Assert.False(Value("{enum:info|warn}", "inf"));
        Assert.False(Value("{enum:info|warn}", ""));
    }

    [Fact]
    public void A_literal_equals_prefix_must_be_present_and_exact_in_front_of_the_value()
    {
        Assert.True(Value("--level={enum:info|warn}", "--level=info"));
        Assert.False(Value("--level={enum:info|warn}", "--level=debug"));
        Assert.False(Value("--level={enum:info|warn}", "--level="));
        Assert.False(Value("--level={enum:info|warn}", "level=info"));
        Assert.False(Value("--level={enum:info|warn}", "--level"));
        Assert.False(Value("--level={enum:info|warn}", "--LEVEL=info"));
        Assert.False(Value("--level={enum:info|warn}", "info"));
        Assert.False(Value("--out={word}", "--out=-x"));
        Assert.True(Value("--out={word}", "--out=x1"));
        Assert.True(Value("--root={path:/var/log/app/}", "--root=/var/log/app/x"));
        Assert.False(Value("--root={path:/var/log/app/}", "--root=/etc/x"));
    }

    [Fact]
    public void A_plain_literal_element_is_compared_exactly_on_every_os()
    {
        Assert.True(Value("status", "status"));
        Assert.False(Value("status", "STATUS"));
        Assert.False(Value("status", "status "));
        Assert.False(Value("status", "stat"));
        Assert.True(Value("status", "status", Win, "C:\\t\\tool.exe"));
        Assert.False(Value("status", "STATUS", Win, "C:\\t\\tool.exe"));
    }

    [Fact]
    public void The_program_matches_by_the_path_case_rule_of_the_os()
    {
        var call = new[] { "/USR/BIN/TAIL", "-n", "5", "/var/log/app/x" };
        Assert.False(GrantMatcher.Matches(Tail, call, Cwd, 30, Linux));
        Assert.True(GrantMatcher.Matches(Tail, call, Cwd, 30, Mac));
        Assert.False(GrantMatcher.Matches(Tail, ["/usr/bin/tail2", "-n", "5", "/var/log/app/x"], Cwd, 30, Mac));
    }

    [Fact]
    public void A_working_directory_the_caller_names_must_equal_the_grants()
    {
        var call = new[] { "/usr/bin/tail", "-n", "5", "/var/log/app/x" };
        Assert.True(GrantMatcher.Matches(Tail, call, "/var/log/app", 30, Linux));
        Assert.False(GrantMatcher.Matches(Tail, call, "/var/log", 30, Linux));
        Assert.False(GrantMatcher.Matches(Tail, call, "/var/log/app/", 30, Linux));
        Assert.False(GrantMatcher.Matches(Tail, call, "/tmp", 30, Linux));
        Assert.False(GrantMatcher.Matches(Tail, call, "", 30, Linux));
        Assert.False(GrantMatcher.Matches(Tail, call, "/VAR/LOG/APP", 30, Linux));
        Assert.True(GrantMatcher.Matches(Tail, call, "/VAR/LOG/APP", 30, Mac));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(3600, false)]
    public void A_timeout_must_be_at_least_one_second_and_at_most_the_grants(int seconds, bool expected) =>
        Assert.Equal(expected, GrantMatcher.Matches(Tail, ["/usr/bin/tail", "-n", "5", "/var/log/app/x"], Cwd, seconds, Linux));

    [Fact]
    public void Under_root_is_a_separator_boundary_check_with_the_os_case_rule()
    {
        Assert.True(GrantMatcher.IsUnderRoot("/var/log/app", "/var/log/app", Linux));
        Assert.True(GrantMatcher.IsUnderRoot("/var/log/app/x", "/var/log/app/", Linux));
        Assert.True(GrantMatcher.IsUnderRoot("/var/log/app", "/var/log/app/", Linux));
        Assert.False(GrantMatcher.IsUnderRoot("/var/log/app-x", "/var/log/app", Linux));
        Assert.False(GrantMatcher.IsUnderRoot("/VAR/log/app/x", "/var/log/app", Linux));
        Assert.True(GrantMatcher.IsUnderRoot("/VAR/log/app/x", "/var/log/app", Mac));
        Assert.True(GrantMatcher.IsUnderRoot("C:\\LOGS\\app\\x", "c:\\logs", Win));
        Assert.False(GrantMatcher.IsUnderRoot("/var/x", "/var", "plan9"));
        Assert.False(GrantMatcher.IsUnderRoot("", "/var", Linux));
        Assert.False(GrantMatcher.IsUnderRoot("/var/x", "", Linux));
    }
}
