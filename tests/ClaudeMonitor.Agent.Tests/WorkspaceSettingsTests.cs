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
    public async Task An_answer_for_the_previous_workspace_that_lands_after_login_leaves_every_switch_off()
    {
        await Answer(Guid.NewGuid()); // the old workspace's answer, written after login saved the new workspace

        Assert.Equal((false, UpdateModes.Off, (string?)null), Switches());
        Assert.Equal("true", fx.Store.Get(ClaudePolicy.WorkspaceKey)); // stored, but not trusted

        await Answer(current); // the next pass, with the new tokens
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
