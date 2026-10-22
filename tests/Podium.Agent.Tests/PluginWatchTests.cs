using System.Net;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The daemon stops when the Claude Code plugin that started it is uninstalled or switched off (ADR-0007).</summary>
public sealed class PluginWatchTests : IDisposable
{
    private const string Id = "aipod@aipod-releases";
    private readonly string claudeDir = Path.Combine(Path.GetTempPath(), "ap-claude-" + Guid.NewGuid().ToString("N"));
    private readonly string pluginData = Path.Combine(Path.GetTempPath(), "ap-plugin-data-" + Guid.NewGuid().ToString("N"));
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

    // ---- Claude Code's plugin list and the scopes ----------------------------------------------------------------------

    private string Registry(params (string Scope, string? Project)[] installs)
    {
        var entries = string.Join(",", installs.Select(i => "{\"scope\":\"" + i.Scope + "\"" + (i.Project is null ? "" : ",\"projectPath\":" + System.Text.Json.JsonSerializer.Serialize(i.Project)) + ",\"version\":\"1\"}"));
        var json = "{\"version\":2,\"plugins\":{\"other@x\":[{\"scope\":\"user\"}]" + (installs.Length > 0 ? ",\"" + Id + "\":[" + entries + "]" : "") + "}}";
        Directory.CreateDirectory(Path.Combine(claudeDir, "plugins"));
        File.WriteAllText(Path.Combine(claudeDir, "plugins", "installed_plugins.json"), json);
        return json;
    }

    private string NewProject()
    {
        var project = Path.Combine(claudeDir, "projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        return project;
    }

    private static void ProjectSettings(string project, string file, string json) => File.WriteAllText(Path.Combine(project, ".claude", file), json);

    [Fact]
    public void A_plugin_missing_from_claude_codes_list_was_uninstalled_even_when_its_data_was_kept()
    {
        Registry(); // readable, other plugins listed, ours not: `plugin uninstall --keep-data`
        Assert.Contains("no longer has the plugin", PluginWatch.Gone(home.Config, dataExistedAtStart: true), StringComparison.Ordinal);
        Registry(("user", null));
        Assert.Null(PluginWatch.Gone(home.Config, true));
    }

    [Fact]
    public void A_list_that_cannot_be_believed_changes_nothing()
    {
        var path = Path.Combine(claudeDir, "plugins", "installed_plugins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var text in new[] { "{ not json", "[]", """{"version":3,"plugins":{}}""", """{"plugins":{}}""", """{"version":2}""", "{\"version\":2,\"plugins\":{\"" + Id + "\":\"yes\"}}", "{\"version\":2,\"plugins\":{\"" + Id + "\":[5]}}" })
        {
            File.WriteAllText(path, text);
            Assert.Null(PluginWatch.Gone(home.Config, true)); // not gone: the daemon stays up
        }

        File.WriteAllText(path, "{ not json");
        Settings(Enabled("false"));
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal); // the older signal still works
    }

    [Fact]
    public void An_install_for_the_account_is_off_only_in_the_users_settings_and_never_for_one_project()
    {
        var project = NewProject();
        Registry(("user", null));
        ProjectSettings(project, "settings.json", Enabled("false"));
        ProjectSettings(project, "settings.local.json", Enabled("false"));
        Assert.Null(PluginWatch.Gone(home.Config, true)); // one project's switch does not count
        Settings(Enabled("false"));
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_install_is_off_when_that_projects_own_settings_say_so_local_before_shared_before_the_users()
    {
        var project = NewProject();
        Registry(("project", project));
        Assert.Null(PluginWatch.Gone(home.Config, true)); // nothing says it is off
        ProjectSettings(project, "settings.json", Enabled("false"));
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal);
        ProjectSettings(project, "settings.local.json", Enabled("true")); // this machine's override wins
        Assert.Null(PluginWatch.Gone(home.Config, true));
        ProjectSettings(project, "settings.local.json", "{}");
        File.Delete(Path.Combine(project, ".claude", "settings.json"));
        Settings(Enabled("false")); // nothing project-side: the user's value stands
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal);
        ProjectSettings(project, "settings.json", Enabled("true")); // the project enables what the account switched off
        Assert.Null(PluginWatch.Gone(home.Config, true));
    }

    [Fact]
    public void A_local_scope_install_is_read_the_same_way_and_a_missing_project_folder_counts_as_still_there()
    {
        var project = NewProject();
        Registry(("local", project));
        ProjectSettings(project, "settings.local.json", Enabled("false"));
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal);
        Registry(("project", Path.Combine(claudeDir, "gone-" + Guid.NewGuid().ToString("N"))));
        Assert.Null(PluginWatch.Gone(home.Config, true));
    }

    [Fact]
    public void Installed_in_several_places_the_plugin_is_gone_only_when_it_is_off_in_all_of_them()
    {
        var one = NewProject();
        var two = NewProject();
        Registry(("user", null), ("project", one), ("project", two));
        ProjectSettings(one, "settings.json", Enabled("false"));
        ProjectSettings(two, "settings.local.json", Enabled("false"));
        Assert.Null(PluginWatch.Gone(home.Config, true)); // the account's install is not off
        Settings(Enabled("false"));
        Assert.Contains("switched off", PluginWatch.Gone(home.Config, true), StringComparison.Ordinal); // all three are
        ProjectSettings(two, "settings.local.json", Enabled("true"));
        Assert.Null(PluginWatch.Gone(home.Config, true)); // one project has it on again
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

    [Fact]
    public async Task Uninstalling_with_the_data_kept_still_ends_the_daemon_once_the_plugin_list_loses_the_plugin()
    {
        Registry(("user", null));
        var run = Start();
        await Running();
        Registry(); // the data directory is still there
        Assert.True(Directory.Exists(pluginData));

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("plugin list no longer has the plugin", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
    }
}
