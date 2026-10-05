using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;

namespace ClaudeMonitor.Agent.Tests;

public sealed class HomeMigrationTests : IDisposable
{
    private const string OldIdentity = """{"machineKey":"machine-key-of-the-test","server":"https://old.invalid","agentId":"11111111-1111-1111-1111-111111111111"}""";

    private readonly TempHome current = new();
    private readonly TempHome legacy = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        current.Dispose();
        legacy.Dispose();
    }

    private AgentConfig WithLegacy => current.Config with { MigrateFrom = legacy.Dir };

    private static string IdentityIn(TempHome h) => Path.Combine(h.Dir, "agent.json");

    private bool Migrate(AgentConfig config) => HomeMigration.Run(config, new AgentLog(config, clock));

    private string Log => File.Exists(current.Config.LogPath) ? File.ReadAllText(current.Config.LogPath) : "";

    [Fact]
    public void An_empty_new_home_and_a_filled_old_one_copies_the_identity_and_leaves_the_old_one_alone()
    {
        File.WriteAllText(IdentityIn(legacy), OldIdentity);
        File.WriteAllText(Path.Combine(legacy.Dir, "agent.db"), "events");
        Assert.True(Migrate(WithLegacy));
        Assert.Equal(OldIdentity, File.ReadAllText(IdentityIn(current)));
        Assert.Equal(OldIdentity, File.ReadAllText(IdentityIn(legacy)));
        Assert.False(File.Exists(Path.Combine(current.Dir, "agent.db")));
        Assert.Contains("migrated agent.json", Log, StringComparison.Ordinal);
        Assert.Equal("https://old.invalid", Identity.Load(current.Config).Server);
    }

    [Fact]
    public void A_new_home_that_already_has_an_identity_is_never_overwritten()
    {
        File.WriteAllText(IdentityIn(legacy), OldIdentity);
        File.WriteAllText(IdentityIn(current), "new");
        Assert.False(Migrate(WithLegacy));
        Assert.Equal("new", File.ReadAllText(IdentityIn(current)));
        Assert.DoesNotContain("migrate", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_home_is_not_migrated()
    {
        File.WriteAllText(IdentityIn(legacy), OldIdentity);
        var explicitHome = AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? current.Dir : null, () => legacy.Dir);
        Assert.Null(explicitHome.MigrateFrom);
        Assert.Equal(legacy.Dir, AgentConfig.FromEnvironment(_ => null, () => legacy.Dir).MigrateFrom);
        Assert.False(Migrate(explicitHome));
        Assert.False(File.Exists(IdentityIn(current)));
    }

    [Fact]
    public void No_old_identity_means_nothing_happens()
    {
        Assert.False(Migrate(WithLegacy));
        Assert.False(File.Exists(IdentityIn(current)));
        Assert.DoesNotContain("could not migrate", Log, StringComparison.Ordinal);
        Assert.False(Migrate(current.Config));
        Assert.False(Migrate(current.Config with { MigrateFrom = Path.Combine(legacy.Dir, "missing") }));
    }

    [Fact]
    public void A_failed_copy_is_logged_and_the_agent_goes_on()
    {
        // An unreadable source makes the copy throw (POSIX only; Windows has no such mode bit).
        File.WriteAllText(IdentityIn(legacy), OldIdentity);
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(IdentityIn(legacy), UnixFileMode.None);
        try
        {
            Assert.False(Migrate(WithLegacy));
        }
        finally
        {
            File.SetUnixFileMode(IdentityIn(legacy), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.False(File.Exists(IdentityIn(current)));
        Assert.Contains("could not migrate agent.json", Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_command_migrates_first_and_status_names_the_home()
    {
        File.WriteAllText(IdentityIn(legacy), OldIdentity);
        var output = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["status"], WithLegacy, new StringReader(""), output, new StringWriter(), clock));
        Assert.Contains($"home: {current.Dir}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("connected: https://old.invalid", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_homes_per_platform()
    {
        Assert.Equal(Path.Combine("/u", ".claude-monitor"), AgentConfig.HomeFor(windows: true, "/u"));
        Assert.Equal(Path.Combine("/u", "Library", "Application Support", "ClaudeMonitor"), AgentConfig.HomeFor(windows: false, "/u"));
        Assert.Equal(Path.Combine("/l", "ClaudeMonitor"), AgentConfig.LegacyHomeFor("/l"));
        Assert.Equal(OperatingSystem.IsWindows(), AgentConfig.LegacyHome() is not null);
    }
}
