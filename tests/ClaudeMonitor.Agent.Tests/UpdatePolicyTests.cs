using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Who may update the agent on its own: the lower of the machine's and the workspace's mode, and where each comes from.</summary>
public sealed class UpdatePolicyTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    [Theory]
    [InlineData("on", "on", "on")]
    [InlineData("on", "check", "check")]
    [InlineData("on", "off", "off")]
    [InlineData("on", null, "off")] // the workspace's cap was never read: fail closed
    [InlineData("on", "bogus", "off")]
    [InlineData("check", "on", "check")]
    [InlineData("check", "check", "check")]
    [InlineData("check", "off", "off")]
    [InlineData("check", null, "off")]
    [InlineData("off", "on", "off")]
    [InlineData("off", "check", "off")]
    [InlineData("off", null, "off")]
    [InlineData("bogus", "on", "off")]
    public void Effective_is_the_lower_of_the_two_and_unknown_is_off(string machine, string? workspace, string expected)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        if (workspace is not null) store.Set(UpdatePolicy.WorkspaceKey, workspace);
        Assert.Equal(expected, UpdatePolicy.Effective(home.Config with { AutoUpdate = machine }, store));
    }

    [Fact]
    public void The_default_is_off()
    {
        Assert.Equal(UpdateModes.Off, home.Config.AutoUpdate);
        Assert.False(home.Config.AutoUpdateFromEnvironment);
    }

    // ---- the workspace's cap, as the daemon's settings poll stores it -----------------------------------------

    private async Task<string?> Poll(string json)
    {
        var fake = new FakeApi().On("GET /api/agent/settings", HttpStatusCode.OK, json);
        using var http = ApiClient.CreateHttp("https://monitor.invalid", fake);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
        using var api = new ApiClient(http, Credentials.For(home.Config), home.Config.ApiCallTimeout);
        using var store = new LocalStore(home.Config.DatabasePath);
        await new Relay(home.Config, store, api, TimeProvider.System).SettingsAsync(CancellationToken.None);
        return store.Get(UpdatePolicy.WorkspaceKey);
    }

    private static string Settings(string? update) =>
        $$"""{"maskSecrets":true,"eventMaxBytes":1000,"workspaceId":"{{Guid.NewGuid()}}"{{(update is null ? "" : $",\"agentUpdate\":{update}")}}}""";

    [Theory]
    [InlineData("\"on\"", "on")]
    [InlineData("\"check\"", "check")]
    [InlineData("\"off\"", "off")]
    [InlineData("\"surprise\"", "off")]
    [InlineData("null", "off")]
    [InlineData(null, "off")] // an old API that has never heard of the setting
    public async Task The_settings_poll_stores_the_workspace_cap_and_an_old_answer_means_off(string? json, string expected)
    {
        Assert.Equal(expected, await Poll(Settings(json)));
    }

    [Fact]
    public async Task A_later_answer_without_the_setting_takes_the_cap_away()
    {
        Assert.Equal("on", await Poll(Settings("\"on\"")));
        Assert.Equal("off", await Poll(Settings(null)));
    }

    [Fact]
    public async Task The_serialised_contract_carries_the_field_the_agent_reads()
    {
        Assert.Equal("on", await Poll(JsonSerializer.Serialize(new AgentSettings(false, 10, Guid.NewGuid(), UpdateModes.On), ApiClient.Json)));
    }

    // ---- the machine's setting --------------------------------------------------------------------------------

    [Fact]
    public void The_machines_setting_is_saved_in_agent_json_and_applied()
    {
        var updated = SavedSettings.SaveAutoUpdate(home.Config, UpdateModes.Check);
        Assert.Equal(UpdateModes.Check, updated.AutoUpdate);
        Assert.Contains("\"autoUpdate\": \"check\"", File.ReadAllText(home.Config.IdentityPath), StringComparison.Ordinal);
        Assert.Equal(UpdateModes.Check, SavedSettings.Apply(home.Config).AutoUpdate);
        Assert.Equal(UpdateModes.Check, Identity.Peek(home.Config)!.AutoUpdate);
    }

    [Fact]
    public void Saving_the_setting_keeps_everything_else_in_the_identity()
    {
        var agent = Guid.NewGuid();
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = agent, StopWaitSeconds = 7, Push = true }).Save(home.Config);
        SavedSettings.SaveAutoUpdate(home.Config, UpdateModes.On);
        var saved = Identity.Peek(home.Config)!;
        Assert.Equal(("https://m.invalid", agent, 7, true, "on"), (saved.Server, saved.AgentId, saved.StopWaitSeconds, saved.Push, saved.AutoUpdate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ON")]
    [InlineData("always")]
    [InlineData(null)]
    public void An_invalid_mode_is_refused_and_nothing_is_saved(string? mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SavedSettings.SaveAutoUpdate(home.Config, mode!));
        Assert.Null(Identity.Peek(home.Config)?.AutoUpdate);
    }

    private AgentConfig FromEnvironment(string? autoUpdate) => AgentConfig.FromEnvironment(key => key switch
    {
        "CM_AGENT_HOME" => home.Dir,
        "CM_CREDENTIALS" => "file",
        "CM_AUTO_UPDATE" => autoUpdate,
        _ => null,
    });

    [Fact]
    public void The_environment_wins_over_the_saved_setting_in_both_directions()
    {
        SavedSettings.SaveAutoUpdate(home.Config, UpdateModes.Off);
        var forcedOn = FromEnvironment("on");
        Assert.True(forcedOn.AutoUpdateFromEnvironment);
        Assert.Equal(UpdateModes.On, SavedSettings.Apply(forcedOn).AutoUpdate);

        SavedSettings.SaveAutoUpdate(home.Config, UpdateModes.On);
        Assert.Equal(UpdateModes.Off, SavedSettings.Apply(FromEnvironment("off")).AutoUpdate);
        Assert.Equal(UpdateModes.Off, SavedSettings.Apply(FromEnvironment("nonsense")).AutoUpdate); // set but invalid: off, and still wins
        Assert.Equal(UpdateModes.On, SavedSettings.Apply(FromEnvironment(null)).AutoUpdate); // unset: the saved one
        Assert.False(FromEnvironment(null).AutoUpdateFromEnvironment);
    }

    [Fact]
    public void An_agent_json_from_before_the_setting_still_loads_and_means_off()
    {
        var saved = JsonSerializer.Serialize(new { machineKey = new string('a', 32), server = "https://m.invalid", agentId = Guid.NewGuid() });
        File.WriteAllText(home.Config.IdentityPath, saved);
        var identity = Identity.Peek(home.Config);
        Assert.NotNull(identity);
        Assert.Null(identity.AutoUpdate);
        Assert.True(identity.Connected);
        Assert.Equal(UpdateModes.Off, SavedSettings.Apply(home.Config).AutoUpdate);
        Assert.Equal(UpdateModes.Off, Identity.Load(home.Config).AutoUpdate ?? UpdateModes.Off);
    }

    [Fact]
    public void A_saved_value_that_is_not_a_mode_is_read_as_off()
    {
        File.WriteAllText(home.Config.IdentityPath, JsonSerializer.Serialize(new { machineKey = new string('a', 32), autoUpdate = "bogus" }));
        Assert.Equal(UpdateModes.Off, SavedSettings.Apply(home.Config with { AutoUpdate = UpdateModes.On }).AutoUpdate);
    }
}
