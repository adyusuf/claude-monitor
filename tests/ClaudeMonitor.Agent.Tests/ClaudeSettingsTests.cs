using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Where the two consents come from: the environment, agent.json, and the workspace's answer the daemon stores.</summary>
public sealed class ClaudeSettingsTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;
    private readonly FakeApi fake = new();
    private readonly HttpClient http;
    private readonly ApiClient api;
    private readonly Relay relay;

    public ClaudeSettingsTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        http = ApiClient.CreateHttp("https://monitor.invalid", fake);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
        relay = new Relay(home.Config, store, api, clock);
    }

    public void Dispose()
    {
        api.Dispose();
        http.Dispose();
        store.Dispose();
        home.Dispose();
    }

    private static AgentConfig Env(Dictionary<string, string> values) => AgentConfig.FromEnvironment(k => values.GetValueOrDefault(k));

    // ---- the environment ---------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_set_means_off_with_the_documented_defaults()
    {
        var c = Env(new() { ["CM_AGENT_HOME"] = home.Dir });
        Assert.False(c.ClaudeUpdateEnabled);
        Assert.False(c.ClaudeUpdateFromEnvironment);
        Assert.Null(c.ClaudeBinary);
        Assert.EndsWith(".claude", c.ClaudeConfigDir, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromHours(24), c.ClaudeUpdateEvery);
        Assert.Equal(TimeSpan.FromMinutes(10), c.ClaudeIdleFor);
        Assert.Equal(TimeSpan.FromMinutes(5), c.ClaudeCountdown);
        Assert.Equal(TimeSpan.FromHours(24), c.ClaudeSnooze);
    }

    [Theory]
    [InlineData("on", true, true)]
    [InlineData("off", false, true)]
    [InlineData("yes", false, true)]
    [InlineData("", false, false)]
    public void CM_CLAUDE_UPDATE_is_on_only_when_exactly_on_and_counts_as_set_when_not_empty(string value, bool enabled, bool fromEnvironment)
    {
        var c = Env(new() { ["CM_AGENT_HOME"] = home.Dir, ["CM_CLAUDE_UPDATE"] = value });
        Assert.Equal((enabled, fromEnvironment), (c.ClaudeUpdateEnabled, c.ClaudeUpdateFromEnvironment));
    }

    [Fact]
    public void The_environment_names_Claude_Codes_folder_the_binary_and_the_path()
    {
        var c = Env(new() { ["CM_AGENT_HOME"] = home.Dir, ["CLAUDE_CONFIG_DIR"] = "/elsewhere/claude", ["CM_CLAUDE_BINARY"] = "/x/claude", ["PATH"] = "/a:/b" });
        Assert.Equal("/elsewhere/claude", c.ClaudeConfigDir);
        Assert.Equal("/x/claude", c.ClaudeBinary);
        Assert.Equal("/a:/b", c.PathVariable);
        Assert.Equal(Path.Combine(home.Dir, "claude-update-state.json"), c.ClaudeUpdateStatePath);
        Assert.Equal(Path.Combine(home.Dir, "claude-update.lock"), c.ClaudeUpdateLockPath);
        Assert.Equal(Path.Combine(home.Dir, "claude-update.cancel"), c.ClaudeCancelPath);
    }

    [Fact]
    public void A_saved_setting_applies_unless_the_environment_set_one()
    {
        var plain = home.Config;
        SavedSettings.SaveClaudeUpdate(plain, on: true);
        Assert.True(SavedSettings.Apply(plain).ClaudeUpdateEnabled);
        Assert.False(SavedSettings.Apply(plain with { ClaudeUpdateFromEnvironment = true }).ClaudeUpdateEnabled);
        Assert.True(SavedSettings.Apply(plain with { ClaudeUpdateFromEnvironment = true, ClaudeUpdateEnabled = true }).ClaudeUpdateEnabled);
        SavedSettings.SaveClaudeUpdate(plain, on: false);
        Assert.False(SavedSettings.Apply(plain with { ClaudeUpdateEnabled = true }).ClaudeUpdateEnabled);
    }

    // ---- both sides --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, "false", false)]
    [InlineData(false, "true", false)]
    [InlineData(true, "true", true)]
    public void Both_the_machine_and_the_workspace_must_allow_it(bool machine, string? workspace, bool allowed)
    {
        SavedSettings.SaveClaudeUpdate(home.Config, machine);
        if (workspace is not null) store.Set(ClaudePolicy.WorkspaceKey, workspace);
        Assert.Equal(allowed, ClaudePolicy.Allowed(home.Config, store));
    }

    [Fact]
    public void The_environment_decides_the_machine_side_when_set()
    {
        store.Set(ClaudePolicy.WorkspaceKey, "true");
        SavedSettings.SaveClaudeUpdate(home.Config, on: false);
        Assert.True(ClaudePolicy.Allowed(home.Config with { ClaudeUpdateFromEnvironment = true, ClaudeUpdateEnabled = true }, store));
        SavedSettings.SaveClaudeUpdate(home.Config, on: true);
        Assert.False(ClaudePolicy.Allowed(home.Config with { ClaudeUpdateFromEnvironment = true, ClaudeUpdateEnabled = false }, store));
    }

    // ---- what the daemon stores of the workspace's answer ------------------------------------------------------

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task The_workspace_answer_is_stored_for_the_updater(bool answer, string stored)
    {
        fake.On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid(), ClaudeUpdate: answer));
        await relay.SettingsAsync(CancellationToken.None);
        Assert.Equal(stored, store.Get(ClaudePolicy.WorkspaceKey));
    }

    [Fact]
    public async Task An_answer_from_an_older_server_without_the_field_is_stored_as_false_and_replaces_an_earlier_true()
    {
        store.Set(ClaudePolicy.WorkspaceKey, "true");
        fake.On("GET /api/agent/settings", HttpStatusCode.OK, $$"""{"maskSecrets":true,"eventMaxBytes":1000,"workspaceId":"{{Guid.NewGuid()}}"}""");
        await relay.SettingsAsync(CancellationToken.None);
        Assert.Equal("false", store.Get(ClaudePolicy.WorkspaceKey));
        Assert.Equal("1000", store.Get("settings.event_max_bytes")); // the rest of the answer still applies
    }

    [Fact]
    public async Task A_failed_settings_call_leaves_the_last_workspace_answer_as_it_was()
    {
        store.Set(ClaudePolicy.WorkspaceKey, "true");
        fake.On("GET /api/agent/settings", HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAnyAsync<Exception>(() => relay.SettingsAsync(CancellationToken.None));
        Assert.Equal("true", store.Get(ClaudePolicy.WorkspaceKey));
    }
}
