using System.ComponentModel;
using System.Runtime.Versioning;
using Aipod.Tray;
using Podium.Agent.Config;

namespace Aipod.Tray.Tests;

/// <summary>The real command runner, against small programs every Unix has; the Windows build of it is the same code path and is not run here.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ProcessTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("aipod-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private AgentConfig Config(string binary) => AgentConfig.FromEnvironment(k => k switch { "AP_AGENT_HOME" => dir, "AP_BINARY" => binary, _ => null });

    [Fact]
    public async Task The_output_and_the_exit_code_of_the_installed_binary_come_back()
    {
        if (OperatingSystem.IsWindows()) return;
        var ok = await new ProcessCli(Config("/bin/echo")).RunAsync(["hello", "world"], CancellationToken.None);
        Assert.Equal((0, "hello world\n"), (ok.ExitCode, ok.Output));

        var failed = await new ProcessCli(Config("/usr/bin/false")).RunAsync([], CancellationToken.None);
        Assert.Equal(1, failed.ExitCode);
    }

    [Fact]
    public async Task A_binary_that_is_missing_throws_a_win32_exception_the_actions_can_report()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<Win32Exception>(() => new ProcessCli(Config(Path.Combine(dir, "nowhere"))).RunAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_killed_and_reported_as_a_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = new ProcessCli(Config("/bin/sleep"), TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TimeoutException>(() => cli.RunAsync(["30"], CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_ends_the_wait_without_calling_it_a_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessCli(Config("/bin/sleep")).RunAsync(["30"], cancel.Token));
    }

    [Fact]
    public async Task Missing_arguments_are_refused() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => new ProcessCli(Config("/bin/echo")).RunAsync(null!, CancellationToken.None));
}

public sealed class AutostartTests
{
    private sealed class FakeTarget : IAutostartTarget
    {
        public List<string> Calls { get; } = [];

        public void WriteFile(string path, string content) => Calls.Add($"write {path}|{content.Length > 0}");

        public void DeleteFile(string path) => Calls.Add($"delete {path}");

        public void SetRunValue(string name, string command) => Calls.Add($"set {name}={command}");

        public void DeleteRunValue(string name) => Calls.Add($"unset {name}");
    }

    [Fact]
    public void The_launch_agent_runs_the_tray_at_login_once_and_escapes_the_path()
    {
        var plist = Autostart.Plist("/Apps/Tom & Jerry/<aipod-tray>");

        Assert.Contains("<key>Label</key><string>aipod.tray</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>/Apps/Tom &amp; Jerry/&lt;aipod-tray&gt;</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>KeepAlive</key><false/>", plist, StringComparison.Ordinal);
        Assert.Equal(Path.Combine("/Users/x", "Library", "LaunchAgents", "aipod.tray.plist"), Autostart.PlistPath("/Users/x"));
    }

    [Fact]
    public void Each_system_gets_its_own_way_and_other_systems_get_a_sentence_and_no_write()
    {
        var target = new FakeTarget();

        Assert.Contains("starts at login", Autostart.Enable(target, "/t/aipod-tray", "/Users/x", AutostartOs.MacOs), StringComparison.Ordinal);
        Assert.Contains("starts at login", Autostart.Enable(target, @"C:\t\aipod-tray.exe", "/Users/x", AutostartOs.Windows), StringComparison.Ordinal);
        Assert.Contains("Windows and macOS only", Autostart.Enable(target, "/t", "/Users/x", AutostartOs.Other), StringComparison.Ordinal);
        Assert.Contains("no longer", Autostart.Disable(target, "/Users/x", AutostartOs.MacOs), StringComparison.Ordinal);
        Assert.Contains("no longer", Autostart.Disable(target, "/Users/x", AutostartOs.Windows), StringComparison.Ordinal);
        Assert.Contains("Windows and macOS only", Autostart.Disable(target, "/Users/x", AutostartOs.Other), StringComparison.Ordinal);

        Assert.Equal(
        [
            $"write {Autostart.PlistPath("/Users/x")}|True",
            @"set aipod-tray=""C:\t\aipod-tray.exe""",
            $"delete {Autostart.PlistPath("/Users/x")}",
            "unset aipod-tray",
        ], target.Calls);
    }

    [Fact]
    public void The_current_system_is_one_of_the_three_and_the_default_follows_it()
    {
        var target = new FakeTarget();
        Autostart.Enable(target, "/t", "/Users/x");
        Assert.Equal(Autostart.Current == AutostartOs.Other ? 0 : 1, target.Calls.Count);
        Assert.Throws<ArgumentNullException>(() => Autostart.Enable(null!, "/t", "/u"));
        Assert.Throws<ArgumentNullException>(() => Autostart.Disable(null!, "/u"));
    }

    [Fact]
    public void The_system_target_writes_and_deletes_files_under_the_given_folder_and_refuses_the_registry_elsewhere()
    {
        using var home = new AgentHome();
        var target = new SystemAutostartTarget();
        var path = Path.Combine(home.Path, "Library", "LaunchAgents", "x.plist");

        target.WriteFile(path, "<plist/>");
        Assert.Equal("<plist/>", File.ReadAllText(path));
        target.DeleteFile(path);
        target.DeleteFile(path); // already gone: not an error
        Assert.False(File.Exists(path));

        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => target.SetRunValue("x", "y"));
        Assert.Throws<PlatformNotSupportedException>(() => target.DeleteRunValue("x"));
    }
}

public sealed class ProgramTests : IDisposable
{
    private readonly AgentHome home = new();

    public void Dispose() => home.Dispose();

    private int Run(string[] args, out string output, out string error, Func<int>? runApp = null)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = Program.Run(args, home.Config, home.Path, "/t/aipod-tray", stdout, stderr, runApp ?? (() => 42));
        (output, error) = (stdout.ToString(), stderr.ToString());
        return code;
    }

    [Fact]
    public void Without_arguments_the_tray_itself_starts_and_its_exit_code_is_returned()
    {
        var started = 0;
        Assert.Equal(7, Run([], out _, out _, () =>
        {
            started++;
            return 7;
        }));
        Assert.Equal(1, started);
    }

    [Fact]
    public void Status_prints_what_the_window_shows()
    {
        home.Connect();

        Assert.Equal(0, Run(["--status"], out var output, out var error));

        Assert.Equal("", error);
        Assert.StartsWith("aipod ", output, StringComparison.Ordinal);
        Assert.Contains("waiting for the agent to start", output, StringComparison.Ordinal);
        Assert.Contains("messages in: 0, out: 0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_prints_the_usage_and_fails_without_starting_anything()
    {
        Assert.Equal(2, Run(["--nope"], out var output, out var error, () => throw new InvalidOperationException("must not start")));
        Assert.Equal("", output);
        Assert.Contains("usage: aipod-tray [--status | --install-autostart | --remove-autostart]", error, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => Program.Run(null!, home.Config, "/", "/t", TextWriter.Null, TextWriter.Null, () => 0));
    }

    [Fact]
    public void The_autostart_options_write_under_the_given_home_only_on_the_systems_that_have_one()
    {
        if (OperatingSystem.IsWindows()) return; // the real registry: not touched by a test
        Assert.Equal(0, Run(["--install-autostart"], out var installed, out _));
        var plist = Autostart.PlistPath(home.Path);
        Assert.Equal(OperatingSystem.IsMacOS(), File.Exists(plist));
        Assert.NotEmpty(installed);

        Assert.Equal(0, Run(["--remove-autostart"], out var removed, out _));
        Assert.False(File.Exists(plist));
        Assert.NotEmpty(removed);
    }
}
