using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A stored settings read tagged with a workspace other than the one agent.json names now: it forces a read.</summary>
public sealed partial class RunRelayTests
{
    // Every read answers `remoteRuns` for the workspace agent.json names when the answer is built.
    private void SettingsFor(bool remoteRuns) =>
        fx.Fake.On("GET /api/agent/settings", _ =>
            (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentSettings(true, 1000, Identity.Peek(fx.Home.Config)?.WorkspaceId ?? Guid.Empty, RemoteRuns: remoteRuns), ApiClient.Json)));

    // One read as the daemon would make it (workspace in agent.json, switch off), stamped at the manual clock's now.
    private async Task<Relay> ReadOffAsync()
    {
        TestWorkspace.Id(fx.Home.Config);
        SettingsFor(remoteRuns: false);
        var timed = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock) { Runs = relay };
        await timed.SettingsAsync(CancellationToken.None);
        fx.Clock.Advance(TimeSpan.FromSeconds(1)); // recent: well inside the reuse window
        return timed;
    }

    [Fact]
    public async Task A_recent_read_tagged_with_the_previous_workspace_is_read_again_after_a_login_and_the_run_executes()
    {
        if (!ExecFixture.Unix) return;
        var timed = await ReadOffAsync(); // tagged W1, switch off
        var moved = Identity.Peek(fx.Home.Config)! with { WorkspaceId = Guid.NewGuid() };
        moved.Save(fx.Home.Config); // `cm-agent login` moved the machine to W2
        SettingsFor(remoteRuns: true);
        Assert.Null(WorkspaceSettings.Get(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey)); // W1's value reads as unread
        Assert.True(WorkspaceSettings.TagIsStale(fx.Home.Config, fx.Store));

        var run = Shell("echo after-login");
        Routes(run.Id);
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));

        Assert.True(await Finished(run.Id));
        Assert.Equal(RunStatuses.Succeeded, fx.Store.ExecRunOf(run.Id.ToString())!.FinalStatus);
        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings"));
        Assert.False(WorkspaceSettings.TagIsStale(fx.Home.Config, fx.Store)); // the forced read re-tagged the store
    }

    [Fact]
    public async Task A_stale_tag_forces_one_read_per_run_not_a_loop_when_the_answer_is_still_off()
    {
        if (!ExecFixture.Unix) return;
        var timed = await ReadOffAsync();
        (Identity.Peek(fx.Home.Config)! with { WorkspaceId = Guid.NewGuid() }).Save(fx.Home.Config);

        var run = Shell("echo off-for-w2");
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));

        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings")); // the first read and exactly one forced read
    }

    [Fact]
    public async Task A_recent_read_tagged_with_the_current_workspace_still_refuses_the_run_without_another_read()
    {
        if (!ExecFixture.Unix) return;
        var timed = await ReadOffAsync(); // tagged with the workspace agent.json still names
        Assert.False(WorkspaceSettings.TagIsStale(fx.Home.Config, fx.Store));

        var run = Shell("echo should-not-run");
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));

        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings")); // only the read before the run
    }

    [Fact]
    public async Task A_recent_read_with_no_identity_in_agent_json_is_not_read_again_by_the_stale_tag_rule()
    {
        if (!ExecFixture.Unix) return;
        var timed = await ReadOffAsync();
        File.Delete(fx.Home.Config.IdentityPath); // not logged in: nothing names a workspace
        Assert.Null(Identity.Peek(fx.Home.Config));
        Assert.False(WorkspaceSettings.TagIsStale(fx.Home.Config, fx.Store));

        var run = Shell("echo should-not-run");
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));

        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings"));
    }

    [Fact]
    public void A_store_that_was_never_read_has_no_stale_tag()
    {
        TestWorkspace.Id(fx.Home.Config);
        Assert.False(WorkspaceSettings.TagIsStale(fx.Home.Config, fx.Store));
    }
}
