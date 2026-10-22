using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>ADR-0013 step 5: `aipod migrate-home` moves the old home to the aipod folder on request, leaves a link, and refuses when unsure. All on temp folders.</summary>
public sealed class HomeMoveTests : IDisposable
{
    private readonly string profile = Path.Combine(Path.GetTempPath(), "ap-move-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();

    public HomeMoveTests() => Directory.CreateDirectory(profile);

    public void Dispose() => Directory.Delete(profile, recursive: true);

    private string Old => HomeResolver.OldHomeFor(OsKinds.Linux, profile);
    private string New => HomeResolver.NewHomeFor(OsKinds.Linux, profile);

    private static AgentConfig ConfigAt(string home) => AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home : null);

    private void FillOld()
    {
        Directory.CreateDirectory(Path.Combine(Old, "bin"));
        File.WriteAllText(Path.Combine(Old, "agent.json"), "{\"server\":\"https://x.invalid\"}");
        File.WriteAllText(Path.Combine(Old, "agent.db"), "database");
        File.WriteAllText(Path.Combine(Old, "bin", "aipod"), "binary");
    }

    private Task<int> Move(AgentConfig config, FakeDaemon daemon, string os = OsKinds.Linux, bool fromVariable = false, params string[] args) =>
        HomeMove.RunAsync(args, config, new HomeMove.Setup(os, profile, fromVariable), daemon, stdout, stderr);

    private FakeDaemon Daemon(AgentConfig config, bool running) => new(config, clock, healthy: false) { Running = running }; // unhealthy: it writes no markers into a database that moved

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    [Fact]
    public async Task The_old_folder_moves_whole_a_link_stays_behind_and_a_running_daemon_is_stopped_and_started_again()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);
        var daemon = Daemon(config, running: true);

        Assert.Equal(0, await Move(config, daemon, fromVariable: false));

        Assert.Equal("database", File.ReadAllText(Path.Combine(New, "agent.db")));
        Assert.Equal("binary", File.ReadAllText(Path.Combine(New, "bin", "aipod")));
        Assert.True(IsLink(Old));
        Assert.Equal("database", File.ReadAllText(Path.Combine(Old, "agent.db"))); // an old binary or wrapper still finds its files
        Assert.Equal(["stop", "start:" + config.BinaryPath], daemon.Calls);
        Assert.Contains("To undo", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public async Task A_daemon_that_was_not_running_is_not_started()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);
        var daemon = Daemon(config, running: false);

        Assert.Equal(0, await Move(config, daemon));

        Assert.Empty(daemon.Calls);
        Assert.True(Directory.Exists(New));
    }

    [Fact]
    public async Task A_dry_run_says_what_it_would_do_and_changes_nothing_not_even_the_daemon()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);
        var daemon = Daemon(config, running: true);

        Assert.Equal(0, await Move(config, daemon, args: HomeMove.DryRunOption));

        Assert.Contains("Would move", stdout.ToString(), StringComparison.Ordinal);
        Assert.False(IsLink(Old));
        Assert.False(Directory.Exists(New));
        Assert.Empty(daemon.Calls);
    }

    [Fact]
    public async Task An_empty_aipod_folder_an_installer_left_behind_does_not_stand_in_the_way()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        Directory.CreateDirectory(New);
        var config = ConfigAt(Old);

        Assert.Equal(0, await Move(config, Daemon(config, running: false)));

        Assert.True(File.Exists(Path.Combine(New, "agent.json")));
    }

    [Fact]
    public async Task It_refuses_and_changes_nothing_when_the_aipod_folder_has_content()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "something"), "x");
        var config = ConfigAt(Old);
        var daemon = Daemon(config, running: true);

        Assert.Equal(2, await Move(config, daemon));

        Assert.Contains("already has content", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(IsLink(Old));
        Assert.Empty(daemon.Calls); // the daemon is not even stopped for a move that cannot happen
    }

    [Fact]
    public async Task A_daemon_that_will_not_stop_means_nothing_is_moved()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);
        var daemon = Daemon(config, running: true);
        daemon.StopResult = false;
        daemon.KillResult = false;

        Assert.Equal(1, await Move(config, daemon));

        Assert.Contains("did not stop", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(IsLink(Old));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public async Task A_daemon_that_took_the_lock_in_the_meantime_means_nothing_is_moved_and_the_stopped_daemon_is_started_again()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);
        var daemon = Daemon(config, running: true);
        using var other = DaemonHost.TryLock(config.LockPath); // someone else holds the daemon's lock
        Assert.NotNull(other);

        Assert.Equal(1, await Move(config, daemon));

        Assert.Contains("took over", stderr.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Old, "agent.json")));
        Assert.False(Directory.Exists(New));
        Assert.Equal(["stop", "start:" + config.BinaryPath], daemon.Calls);
    }

    [Fact]
    public async Task It_refuses_a_home_from_a_variable_a_boot_service_windows_and_a_home_that_is_somewhere_else()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);

        Assert.Equal(2, await Move(config, Daemon(config, false), fromVariable: true));
        Assert.Equal(2, await Move(config with { ServiceMode = true }, Daemon(config, false)));
        Assert.Equal(2, await Move(config, Daemon(config, false), os: OsKinds.Windows));
        Assert.Equal(2, await Move(ConfigAt(Path.Combine(profile, "elsewhere")), Daemon(config, false)));

        var text = stderr.ToString();
        foreach (var expected in new[] { "AP_AGENT_HOME", "boot service", "Not on Windows", "neither the old folder" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.False(IsLink(Old));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public async Task A_machine_that_is_already_on_the_aipod_folder_or_already_linked_has_nothing_to_do_and_that_is_not_a_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        FillOld();
        var config = ConfigAt(Old);
        Assert.Equal(0, await Move(config, Daemon(config, false))); // the move itself

        Assert.Equal(0, await Move(ConfigAt(New), Daemon(config, false)));
        Assert.Equal(0, await Move(config, Daemon(config, false))); // the old path is now a link: refused, harmlessly
        Assert.Contains("Already using the aipod folder", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Already moved", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_old_folder_is_refused_with_a_clear_message()
    {
        if (OperatingSystem.IsWindows()) return;
        var config = ConfigAt(Old);

        Assert.Equal(2, await Move(config, Daemon(config, false)));

        Assert.Contains("There is no folder", stderr.ToString(), StringComparison.Ordinal);
    }
}
