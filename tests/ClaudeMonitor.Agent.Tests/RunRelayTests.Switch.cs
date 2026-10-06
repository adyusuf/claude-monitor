using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The workspace's remote-runs switch as the relay applies it to a run from the stream.</summary>
public sealed partial class RunRelayTests
{
    [Theory]
    [InlineData(false)] // never read
    [InlineData(true)] // read, but for another workspace than the one in agent.json
    public async Task A_remote_runs_switch_that_is_unread_or_another_workspaces_fails_the_run_as_disabled(bool otherWorkspace)
    {
        if (!ExecFixture.Unix) return;
        if (otherWorkspace)
        {
            TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, "true");
            WorkspaceSettings.Tag(fx.Store, Guid.NewGuid());
        }

        Settings(remoteRuns: false);
        var run = Shell($"echo should-not-run > {Path.Combine(fx.Home.Dir, "ran")}");
        Routes(run.Id);

        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        Assert.Equal(0, Volatile.Read(ref guardCalls));
        Assert.False(File.Exists(Path.Combine(fx.Home.Dir, "ran")));
    }

    [Fact]
    public async Task A_switch_turned_on_since_the_last_settings_pass_is_read_again_and_the_run_executes()
    {
        if (!ExecFixture.Unix) return;
        TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, "false"); // read before the admin turned it on
        Settings(remoteRuns: true);
        var run = Shell("echo late-switch");
        Routes(run.Id);

        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.True(await Finished(run.Id));
        Assert.Equal(RunStatuses.Succeeded, fx.Store.ExecRunOf(run.Id.ToString())!.FinalStatus);
        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings"));
    }

    [Fact]
    public async Task A_burst_of_runs_while_the_switch_is_off_costs_one_settings_read()
    {
        if (!ExecFixture.Unix) return;
        Settings(remoteRuns: false);
        foreach (var run in new[] { Shell("echo 1"), Shell("echo 2"), Shell("echo 3") })
        {
            Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
            Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        }

        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings"));
    }

    [Fact]
    public async Task A_burst_of_runs_all_decided_before_the_settings_read_costs_one_read()
    {
        if (!ExecFixture.Unix) return;
        Settings(remoteRuns: false);
        var decided = DateTimeOffset.UtcNow.AddMinutes(-1); // well before the read, whatever a few seconds of clock skew
        foreach (var run in new[] { Shell("echo 1", decided), Shell("echo 2", decided), Shell("echo 3", decided) })
        {
            Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
            Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        }

        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings"));
    }

    [Fact]
    public async Task A_run_decided_after_the_last_settings_read_forces_a_new_read_and_executes_when_the_switch_is_now_on()
    {
        if (!ExecFixture.Unix) return;
        Settings(remoteRuns: false);
        var early = Shell("echo early", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(early), CancellationToken.None));
        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(early.Id.ToString())!.Error);
        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings"));

        // the admin turns the switch on; the server approves a run after the read the agent still holds (under 10 s old)
        Settings(remoteRuns: true);
        var late = Shell("echo decided-late", DateTimeOffset.UtcNow);
        Routes(late.Id);
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(late), CancellationToken.None));

        Assert.True(await Finished(late.Id));
        Assert.Equal(RunStatuses.Succeeded, fx.Store.ExecRunOf(late.Id.ToString())!.FinalStatus);
        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings"));
    }

    [Fact]
    public async Task A_run_decided_after_the_read_is_still_refused_when_the_switch_reads_off_again()
    {
        if (!ExecFixture.Unix) return;
        Settings(remoteRuns: false);
        var run = Shell("echo no", DateTimeOffset.UtcNow.AddSeconds(1)); // a server clock a little ahead of this one
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        Assert.Equal(1, fx.Fake.Count("GET /api/agent/settings"));
        Assert.Equal(0, Volatile.Read(ref guardCalls));
    }

    [Fact]
    public async Task A_settings_read_that_fails_leaves_the_run_unrecorded_so_the_replay_can_run_it()
    {
        if (!ExecFixture.Unix) return;
        fx.Fake.On("GET /api/agent/settings", HttpStatusCode.BadGateway, "{}");
        var run = Shell("echo later");

        await Assert.ThrowsAnyAsync<Exception>(() => stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.Null(fx.Store.ExecRunOf(run.Id.ToString()));
        Assert.Equal(0, Volatile.Read(ref guardCalls));
    }

    [Fact]
    public async Task With_remote_runs_switched_off_locally_a_run_is_failed_as_disabled_without_executing()
    {
        if (!ExecFixture.Unix) return;
        TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, "false");
        Settings(remoteRuns: false); // read again before refusing: still off
        var run = Shell($"echo should-not-run > {Path.Combine(fx.Home.Dir, "ran")}");
        Routes(run.Id);

        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        var row = fx.Store.ExecRunOf(run.Id.ToString())!;
        Assert.Equal((LocalStore.ExecStates.Finished, RunStatuses.Failed, RemoteErrors.Disabled), (row.State, row.FinalStatus, row.Error));
        Assert.Equal(0, Volatile.Read(ref guardCalls));
        Assert.False(File.Exists(Path.Combine(fx.Home.Dir, "ran")));

        await relay.ReportAsync(CancellationToken.None);
        Assert.Contains(fx.Fake.Seen, s => s.Path.EndsWith("/status", StringComparison.Ordinal) && s.Body.Contains(RemoteErrors.Disabled, StringComparison.Ordinal));
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None)); // a replay changes nothing
        Assert.Equal(1, fx.Fake.Count($"POST /api/agent/runs/{run.Id}/status"));
    }

    [Fact]
    public async Task A_settings_read_whose_answer_arrived_after_the_decision_is_not_trusted_for_a_run_decided_while_it_was_in_flight()
    {
        if (!ExecFixture.Unix) return;
        var started = fx.Clock.GetUtcNow();
        var reads = 0;
        // The request reaches the server at once (switch off); the answer is slow: the clock is 4 s on when it completes.
        fx.Fake.On("GET /api/agent/settings", _ =>
        {
            var on = Interlocked.Increment(ref reads) > 1;
            if (!on) fx.Clock.Advance(TimeSpan.FromSeconds(4));
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentSettings(true, 1000, TestWorkspace.Id(fx.Home.Config), RemoteRuns: on), ApiClient.Json));
        });
        var timed = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock) { Runs = relay };
        await timed.SettingsAsync(CancellationToken.None);
        Assert.Equal(started.AddSeconds(4), fx.Clock.GetUtcNow());

        // an admin turned the switch on and the server approved this run a second into that request, before the answer completed
        var run = Shell("echo decided-in-flight", started.AddSeconds(1));
        Routes(run.Id);
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));

        Assert.True(await Finished(run.Id));
        Assert.Equal(RunStatuses.Succeeded, fx.Store.ExecRunOf(run.Id.ToString())!.FinalStatus);
        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings"));
    }
}
