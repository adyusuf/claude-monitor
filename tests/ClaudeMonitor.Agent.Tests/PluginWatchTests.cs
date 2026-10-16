using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The daemon stops when the Claude Code plugin that started it is uninstalled or switched off (ADR-0007).</summary>
public sealed class PluginWatchTests : IDisposable
{
    private const string Id = "monitor-agent@monitor-agent-releases";
    private readonly string claudeDir = Path.Combine(Path.GetTempPath(), "cm-claude-" + Guid.NewGuid().ToString("N"));
    private readonly string pluginData = Path.Combine(Path.GetTempPath(), "cm-plugin-data-" + Guid.NewGuid().ToString("N"));
    private readonly TempHome home;
    private readonly FakeApi fake = new();
    private readonly CancellationTokenSource cancel = new(TimeSpan.FromSeconds(60)); // a safety net only: every test ends the daemon itself

    public PluginWatchTests()
    {
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(pluginData);
        home = new TempHome(c => c with
        {
            FlushEvery = TimeSpan.FromMilliseconds(20),
            HeartbeatEvery = TimeSpan.FromMilliseconds(50),
            StopPollEvery = TimeSpan.FromMilliseconds(20),
            PluginWatchEvery = TimeSpan.FromMilliseconds(30),
            PluginId = Id,
            PluginDataDir = pluginData,
            ClaudeConfigDir = claudeDir,
        });
        fake.On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "")
            .On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid()))
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(50);
                return (HttpStatusCode.OK, "");
            });
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
    }

    public void Dispose()
    {
        cancel.Cancel();
        cancel.Dispose();
        home.Dispose();
        foreach (var dir in new[] { claudeDir, pluginData })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private Task<int> Start(AgentConfig? config = null) =>
        new DaemonHost(config ?? home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake).RunAsync(cancel.Token);

    private async Task Running()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        await Until.True(() => store.Get(Relay.LastContactKey) is not null);
    }

    private static string Enabled(string value, string before = "") => "{\"enabledPlugins\":{" + before + "\"" + Id + "\":" + value + "}}";

    private void Settings(string json) => File.WriteAllText(Path.Combine(claudeDir, "settings.json"), json);

    // ---- the check ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_plugin_that_is_there_and_enabled_is_not_gone_and_unreadable_settings_count_as_there()
    {
        Assert.Null(PluginWatch.Gone(home.Config, dataExistedAtStart: true));
        Settings(Enabled("true", "\"other@x\":false,"));
        Assert.Null(PluginWatch.Gone(home.Config, true));
        Settings("{ this is not json");
        Assert.Null(PluginWatch.Gone(home.Config, true));
        Settings("""{"enabledPlugins":["a"]}""");
        Assert.Null(PluginWatch.Gone(home.Config, true));
        Settings(Enabled("\"false\"")); // only a real false switches it off
        Assert.Null(PluginWatch.Gone(home.Config, true));
    }

    [Fact]
    public void A_deleted_data_directory_or_a_false_in_enabledplugins_means_gone_but_a_directory_that_never_existed_means_nothing()
    {
        Settings(Enabled("false"));
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal);
        Settings("{}");
        Directory.Delete(pluginData);
        Assert.Contains("uninstalled", PluginWatch.Gone(home.Config, dataExistedAtStart: true), StringComparison.Ordinal);
        Assert.Null(PluginWatch.Gone(home.Config, dataExistedAtStart: false));
        Assert.False(PluginWatch.DataExists(home.Config));
        Assert.Null(PluginWatch.Gone(home.Config with { PluginId = null, PluginDataDir = null }, true)); // not started by the plugin
    }

    // ---- the daemon -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Uninstalling_the_plugin_ends_the_daemon_and_frees_the_lock()
    {
        var run = Start();
        await Running();
        Directory.Delete(pluginData);

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(cancel.IsCancellationRequested);
        using var free = DaemonHost.TryLock(home.Config.LockPath);
        Assert.NotNull(free);
        Assert.Contains("stopping: the plugin's data directory is gone", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_the_plugin_off_ends_the_daemon()
    {
        var run = Start();
        await Running();
        Settings(Enabled("false"));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("switched off", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_that_the_plugin_did_not_start_ignores_the_plugin_and_keeps_running()
    {
        var plain = home.Config with { PluginId = null, PluginDataDir = null };
        Settings(Enabled("false"));
        var run = Start(plain);
        await Running();
        using var store = new LocalStore(home.Config.DatabasePath);
        var beats = fake.Count("POST /api/agent/heartbeat");
        await Until.True(() => fake.Count("POST /api/agent/heartbeat") >= beats + 3);
        Assert.False(run.IsCompleted);
        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}
