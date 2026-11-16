using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.ClaudeUpdate;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The agent's own check before it starts Claude or changes its setup from the web (F-201): the workspace's remote-work switch and the owner's opt-in, as its
/// settings pass last stored them, are checked again here, fail-closed, and a stop is never held back.
/// </summary>
public sealed class ClaudeStartGuardTests : IDisposable
{
    private const string Project = "git:host/shop";
    private readonly RemoteFixture fx = new();

    public void Dispose() => fx.Dispose();

    private string? Refusal(string? remote, string? control)
    {
        if (remote is not null) TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, remote);
        if (control is not null) TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.ClaudeControlKey, control);
        return ClaudeStartGuard.Refusal(fx.Home.Config, fx.Store);
    }

    [Theory]
    [InlineData("true", "true", true)]
    [InlineData("true", "unknown", true)]
    [InlineData("true", "false", false)]
    [InlineData("true", "garbage", false)]
    [InlineData("true", null, false)]
    [InlineData("false", "true", false)]
    [InlineData(null, "true", false)]
    public void Both_switches_must_be_on_as_last_read_and_an_opt_in_the_server_did_not_say_is_left_to_the_api(string? remote, string? control, bool allowed) =>
        Assert.Equal(allowed, Refusal(remote, control) is null);

    [Fact]
    public void A_switch_stored_for_another_workspace_reads_as_unread_and_refuses()
    {
        TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, "true");
        TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.ClaudeControlKey, "true");
        Assert.Null(ClaudeStartGuard.Refusal(fx.Home.Config, fx.Store));

        WorkspaceSettings.Tag(fx.Store, Guid.NewGuid());

        Assert.NotNull(ClaudeStartGuard.Refusal(fx.Home.Config, fx.Store));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(null, "unknown")]
    public void The_settings_pass_stores_what_the_api_said_about_the_opt_in(bool? told, string stored)
    {
        var entries = MachineMonitor.Entries(new AgentSettings(true, 1000, Guid.NewGuid(), ClaudeControl: told));

        Assert.Equal(stored, entries.Single(e => e.Key == MachineMonitor.ClaudeControlKey).Value);
    }

    [Fact]
    public void The_stored_opt_in_is_one_of_the_switches_login_and_logout_forget()
    {
        Assert.Contains(MachineMonitor.ClaudeControlKey, WorkspaceSettings.Switches);
    }

    // ---- through the action relay ------------------------------------------------------------------------------

    private ConcurrentQueue<AgentActionReport> Reports(params AgentActionMessage[] actions)
    {
        var reports = new ConcurrentQueue<AgentActionReport>();
        foreach (var action in actions)
        {
            fx.Fake.On($"POST /api/agent/actions/{action.Id}/status", body =>
            {
                reports.Enqueue(JsonSerializer.Deserialize<AgentActionReport>(body, ApiClient.Json)!);
                return (HttpStatusCode.NoContent, "");
            });
        }

        return reports;
    }

    private ActionRelay Relay(Func<string?>? refusal, ClaudeControl? control = null) =>
        new(fx.Api, new AgentLog(fx.Home.Config, TimeProvider.System), () => Task.FromResult("s"), _ => Task.FromResult(new UpdateCheck(UpdateCodes.UpToDate, "ok")),
            _ => true, false, control, null, refusal);

    private static AgentActionMessage Message(string kind, string? session = null) =>
        new(Guid.NewGuid(), kind, DateTimeOffset.UtcNow.AddMinutes(10), Project, SessionRef: session);

    [Fact]
    public async Task A_start_and_a_plugin_or_setting_change_the_agent_refuses_locally_are_reported_failed_in_words_and_nothing_is_run()
    {
        var start = Message(AgentActionKinds.ClaudeStart);
        var plugin = Message(AgentActionKinds.PluginInstall);
        var setting = Message(AgentActionKinds.ProjectSetting);
        var reports = Reports(start, plugin, setting);
        var relay = Relay(() => "refused on this machine: because");

        foreach (var action in new[] { start, plugin, setting }) await relay.PerformAsync(action, CancellationToken.None);

        var finals = reports.Where(r => r.Status != AgentActionStatuses.Delivered).ToList();
        Assert.Equal(3, finals.Count);
        Assert.All(finals, r => Assert.Equal((AgentActionStatuses.Failed, "refused on this machine: because"), (r.Status, r.Result)));
    }

    private sealed class CountingClaude : IClaudeCli
    {
        public List<string[]> Runs { get; } = [];

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add([.. args]);
            return Task.FromResult(args[0] == "--bg" ? new ClaudeRun(0, "backgrounded · 34a6ea9d · aipod-shop\n", "") : new ClaudeRun(0, $"stopped {args[1]}\n", ""));
        }
    }

    [Fact]
    public async Task A_start_the_guard_allows_reaches_claude_bg_and_one_it_refuses_never_does_and_a_stop_is_never_held_back()
    {
        var folder = Directory.CreateTempSubdirectory("aipod-shop-").FullName;
        using var work = new TempHome();
        using (var store = new LocalStore(work.Config.DatabasePath)) store.SeeFolder(Project, folder, DateTimeOffset.UtcNow);
        var cli = new CountingClaude();
        var control = new ClaudeControl(work.Config, cli, TimeProvider.System, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install"));
        var refused = Message(AgentActionKinds.ClaudeStart);
        var allowed = Message(AgentActionKinds.ClaudeStart);
        var stop = Message(AgentActionKinds.ClaudeStop, "34a6ea9d");
        var reports = Reports(refused, allowed, stop);

        await Relay(() => "refused on this machine: because", control).PerformAsync(refused, CancellationToken.None);
        Assert.Empty(cli.Runs);
        await Relay(() => null, control).PerformAsync(allowed, CancellationToken.None);
        Assert.Equal("--bg", Assert.Single(cli.Runs)[0]);
        await Relay(() => "refused on this machine: because", control).PerformAsync(stop, CancellationToken.None);

        Assert.Equal(["--bg", "stop"], cli.Runs.Select(r => r[0]));
        Assert.Equal(AgentActionStatuses.Succeeded, reports.Last().Status);
    }
}
