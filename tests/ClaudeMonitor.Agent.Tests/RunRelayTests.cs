using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed partial class RunRelayTests : IDisposable
{
    private readonly RemoteFixture fx = new();
    private readonly List<string> stateInGuard = [];
    private int guardCalls;
    private readonly RunRelay relay;
    private readonly Relay stream;

    public RunRelayTests()
    {
        var executor = new RunExecutor(fx.Home.Config with { ExecKillGrace = TimeSpan.FromMilliseconds(300) }, TimeProvider.System,
            new AgentLog(fx.Home.Config, TimeProvider.System), run =>
            {
                Interlocked.Increment(ref guardCalls);
                lock (stateInGuard) stateInGuard.Add(fx.Store.ExecRunOf(run.Id.ToString())?.State ?? "unknown");
                return new ExecDecision(true, null, run.Mode == RunModes.Shell ? "/bin/sh" : run.Argv![0]);
            }, () => false);
        relay = new RunRelay(fx.Home.Config, fx.Store, fx.Api, TimeProvider.System, executor, new AgentLog(fx.Home.Config, TimeProvider.System));
        stream = new Relay(fx.Home.Config, fx.Store, fx.Api, TimeProvider.System) { Runs = relay };
    }

    public void Dispose()
    {
        relay.Dispose();
        fx.Dispose();
    }

    /// <summary>What the API answers when the relay reads the workspace's settings again before refusing a run.</summary>
    private void Settings(bool remoteRuns) =>
        fx.Fake.On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, TestWorkspace.Id(fx.Home.Config), RemoteRuns: remoteRuns));

    private void Routes(Guid id, HttpStatusCode output = HttpStatusCode.NoContent)
    {
        fx.Fake.On($"POST /api/agent/runs/{id}/status", RemoteFixture.NoContent);
        fx.Fake.On($"POST /api/agent/runs/{id}/output", _ => (output, ""));
    }

    private static JsonElement Message(RunMessage run) => JsonSerializer.SerializeToElement(run, ApiClient.Json);

    private static RunMessage Shell(string command, DateTimeOffset? decidedAt = null) =>
        new(Guid.NewGuid(), RunModes.Shell, null, command, null, 60, DateTimeOffset.UtcNow.AddMinutes(5), null, null, decidedAt);

    private Task<bool> Finished(Guid id) => ExecHarness.UntilAsync(() => fx.Store.ExecRunOf(id.ToString())?.State == LocalStore.ExecStates.Finished);

    [Fact]
    public async Task A_run_from_the_stream_is_recorded_before_it_starts_and_a_replay_never_starts_it_twice()
    {
        if (!ExecFixture.Unix) return;
        TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, "true");
        var run = Shell("echo once");
        Routes(run.Id);
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.True(await Finished(run.Id));

        Assert.Equal(["running"], stateInGuard); // the row existed, as running, when the executor first looked at the run
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.False(relay.Accept(run, CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref guardCalls));
        Assert.Equal(RunStatuses.Succeeded, fx.Store.ExecRunOf(run.Id.ToString())!.FinalStatus);
    }

    [Fact]
    public async Task A_run_already_in_the_database_is_not_started_at_all()
    {
        if (!ExecFixture.Unix) return;
        var run = Shell("echo never");
        Assert.True(fx.Store.ExecBegin(run.Id.ToString(), DateTimeOffset.UtcNow));
        Assert.False(relay.Accept(run, CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref guardCalls));
        Assert.Empty(fx.Fake.Seen);
    }

    [Fact]
    public async Task A_run_left_running_by_a_stopped_daemon_is_failed_as_agent_restarted_but_one_running_now_is_left_alone()
    {
        if (!ExecFixture.Unix) return;
        var stale = Guid.NewGuid().ToString();
        fx.Store.ExecBegin(stale, DateTimeOffset.UtcNow);
        var live = Shell("sleep 300");
        Routes(live.Id);
        Assert.True(relay.Accept(live, CancellationToken.None));
        Assert.True(await ExecHarness.UntilAsync(() => relay.Running == 1 && Volatile.Read(ref guardCalls) == 1));

        relay.Recover();
        var recovered = fx.Store.ExecRunOf(stale)!;
        Assert.Equal((LocalStore.ExecStates.Finished, RunStatuses.Failed, "agent_restarted"), (recovered.State, recovered.FinalStatus, recovered.Error));
        Assert.Equal(LocalStore.ExecStates.Running, fx.Store.ExecRunOf(live.Id.ToString())!.State);

        relay.Cancel(live.Id);
        Assert.True(await Finished(live.Id));
    }

    [Fact]
    public async Task Output_goes_up_before_the_final_status_and_what_was_sent_is_deleted()
    {
        if (!ExecFixture.Unix) return;
        var run = Shell("echo result-line");
        Routes(run.Id);
        relay.Accept(run, CancellationToken.None);
        Assert.True(await Finished(run.Id));
        Assert.NotEmpty(fx.Store.UnsentExecOutput(10));

        await relay.ReportAsync(CancellationToken.None);

        var calls = fx.Fake.Seen.Select(s => (s.Path, s.Body)).ToList();
        var output = calls.FindIndex(c => c.Path.EndsWith("/output", StringComparison.Ordinal));
        var final = calls.FindIndex(c => c.Path.EndsWith("/status", StringComparison.Ordinal) && c.Body.Contains("\"succeeded\"", StringComparison.Ordinal));
        Assert.True(output >= 0 && final > output, $"output at {output}, final status at {final}");
        Assert.Contains("result-line", calls[output].Body, StringComparison.Ordinal);
        Assert.Empty(fx.Store.UnsentExecOutput(10));
        Assert.True(fx.Store.ExecRunOf(run.Id.ToString())!.Reported);

        var before = fx.Fake.Seen.Count;
        await relay.ReportAsync(CancellationToken.None);
        Assert.Equal(before, fx.Fake.Seen.Count); // reported once
    }

    [Fact]
    public async Task A_failed_output_upload_keeps_the_output_and_holds_back_the_final_status_until_it_goes_through()
    {
        if (!ExecFixture.Unix) return;
        var run = Shell("echo keep-me");
        Routes(run.Id, HttpStatusCode.BadGateway);
        relay.Accept(run, CancellationToken.None);
        Assert.True(await Finished(run.Id));

        await Assert.ThrowsAsync<ApiException>(() => relay.ReportAsync(CancellationToken.None));
        Assert.NotEmpty(fx.Store.UnsentExecOutput(10));
        Assert.DoesNotContain(fx.Fake.Seen, s => s.Body.Contains("\"succeeded\"", StringComparison.Ordinal));

        Routes(run.Id);
        await relay.ReportAsync(CancellationToken.None);
        Assert.Empty(fx.Store.UnsentExecOutput(10));
        Assert.Contains(fx.Fake.Seen, s => s.Body.Contains("\"succeeded\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Output_the_api_no_longer_knows_a_run_for_is_dropped_and_the_outcome_is_still_reported()
    {
        if (!ExecFixture.Unix) return;
        var run = Shell("echo orphan");
        Routes(run.Id, HttpStatusCode.NotFound);
        relay.Accept(run, CancellationToken.None);
        Assert.True(await Finished(run.Id));

        await relay.ReportAsync(CancellationToken.None);
        Assert.Empty(fx.Store.UnsentExecOutput(10));
        Assert.True(fx.Store.ExecRunOf(run.Id.ToString())!.Reported);
    }

    [Fact]
    public async Task A_relay_without_a_run_relay_also_fails_a_run_as_disabled_instead_of_dropping_it()
    {
        if (!ExecFixture.Unix) return;
        var plain = new Relay(fx.Home.Config, fx.Store, fx.Api, TimeProvider.System);
        var run = Shell("echo x");
        Assert.True(await plain.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
    }

    [Fact]
    public async Task A_run_cancel_message_stops_a_running_run()
    {
        if (!ExecFixture.Unix) return;
        TestWorkspace.Set(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey, "true");
        var run = Shell("sleep 300");
        Routes(run.Id);
        await stream.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None);
        Assert.True(await ExecHarness.UntilAsync(() => Volatile.Read(ref guardCalls) == 1));

        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.RunCancel, JsonSerializer.SerializeToElement(new RunCancelMessage(run.Id), ApiClient.Json), CancellationToken.None));
        Assert.True(await Finished(run.Id));
        Assert.Equal(RunStatuses.Cancelled, fx.Store.ExecRunOf(run.Id.ToString())!.FinalStatus);
    }

    [Fact]
    public async Task A_cancel_for_a_run_that_is_not_running_does_nothing()
    {
        if (!ExecFixture.Unix) return;
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.RunCancel, JsonSerializer.SerializeToElement(new RunCancelMessage(Guid.NewGuid()), ApiClient.Json), CancellationToken.None));
        Assert.Empty(fx.Fake.Seen);
    }

    [Fact]
    public async Task A_run_update_marks_the_followed_run_dirty_so_it_is_fetched_without_waiting_for_the_poll()
    {
        if (!ExecFixture.Unix) return;
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        fx.Store.FollowRun(id, RunStatuses.Approved, now);
        fx.Store.RunFetched(id, RunStatuses.Approved, "{}", 0, false, now);
        var longAgo = now.AddHours(-1);
        Assert.Empty(fx.Store.RunsToFetch(longAgo, 10)); // fetched just now and not stale

        var message = JsonSerializer.SerializeToElement(new RunUpdateMessage(Guid.Parse(id), RunStatuses.Running), ApiClient.Json);
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.RunUpdate, message, CancellationToken.None));
        Assert.Equal(id, Assert.Single(fx.Store.RunsToFetch(longAgo, 10)).RunId);
    }

    [Fact]
    public async Task Unknown_stream_events_are_ignored_and_keep_the_stream_open()
    {
        Assert.True(await stream.OnStreamAsync("something_new", JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
    }
}
