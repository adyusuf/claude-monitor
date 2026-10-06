using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// The workspace's switches count only for the workspace in agent.json: a settings answer for another workspace (a pass
/// still in flight while `cm-agent login` moved the machine) leaves every switch off until the new workspace answers.
/// </summary>
public sealed class WorkspaceSettingsTests : IDisposable
{
    private readonly RemoteFixture fx = new();
    private readonly Relay relay;
    private readonly Guid current = Guid.NewGuid();

    public WorkspaceSettingsTests()
    {
        relay = new Relay(fx.Home.Config, fx.Store, fx.Api, TimeProvider.System);
        (Identity.Load(fx.Home.Config) with { WorkspaceId = current }).Save(fx.Home.Config);
        SavedSettings.SaveClaudeUpdate(fx.Home.Config, true);
    }

    public void Dispose() => fx.Dispose();

    private AgentConfig Config => fx.Home.Config with { AutoUpdate = UpdateModes.On };

    private async Task Answer(Guid workspace)
    {
        fx.Fake.On("GET /api/agent/settings", HttpStatusCode.OK,
            new AgentSettings(true, 1000, workspace, UpdateModes.On, RemoteRuns: true, ClaudeUpdate: true));
        await relay.SettingsAsync(CancellationToken.None);
    }

    private (bool Claude, string Agent, string? Runs) Switches() =>
        (ClaudePolicy.Allowed(Config, fx.Store), UpdatePolicy.Effective(Config, fx.Store),
            WorkspaceSettings.Get(Config, fx.Store, MachineMonitor.RemoteRunsKey));

    [Fact]
    public async Task An_answer_for_the_workspace_in_agent_json_turns_the_switches_on()
    {
        await Answer(current);
        Assert.Equal((true, UpdateModes.On, "true"), Switches());
        Assert.Equal(current.ToString("D"), fx.Store.Get(WorkspaceSettings.WorkspaceKey));
    }

    [Fact]
    public async Task A_settings_pass_that_a_login_overtakes_leaves_every_switch_off_until_the_next_pass()
    {
        var next = Guid.NewGuid();
        fx.Fake.On("GET /api/agent/settings", _ =>
        {
            // `cm-agent login` to another workspace lands while the old workspace's answer is on its way
            (Identity.Load(fx.Home.Config) with { WorkspaceId = next }).Save(fx.Home.Config);
            return (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(
                new AgentSettings(true, 1000, current, UpdateModes.On, RemoteRuns: true, ClaudeUpdate: true), Net.ApiClient.Json));
        });
        await relay.SettingsAsync(CancellationToken.None);

        Assert.Equal((false, UpdateModes.Off, (string?)null), Switches());
        Assert.Equal("true", fx.Store.Get(ClaudePolicy.WorkspaceKey)); // stored, but not trusted

        await Answer(next); // the next pass, with the new tokens and agent.json
        Assert.Equal((true, UpdateModes.On, "true"), Switches());
    }

    [Fact]
    public async Task An_agent_moved_to_another_workspace_on_the_web_keeps_trusting_what_the_server_answers()
    {
        await Answer(Guid.NewGuid()); // same tokens, agent.json still names the workspace it logged in to
        Assert.Equal((true, UpdateModes.On, "true"), Switches());
    }

    [Fact]
    public void Switches_stored_without_a_workspace_tag_or_without_an_identity_read_as_unread()
    {
        fx.Store.Set(ClaudePolicy.WorkspaceKey, "true"); // as an agent before this change stored it
        Assert.Null(WorkspaceSettings.Get(Config, fx.Store, ClaudePolicy.WorkspaceKey));

        WorkspaceSettings.Tag(fx.Store, current);
        File.Delete(fx.Home.Config.IdentityPath);
        Assert.Null(WorkspaceSettings.Get(Config, fx.Store, ClaudePolicy.WorkspaceKey));
    }
}
