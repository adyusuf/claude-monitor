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
public sealed class RunRelayReportTests : IDisposable
{
    private readonly RemoteFixture fx = new();
    private readonly List<string> stateInGuard = [];
    private int guardCalls;
    private readonly RunRelay relay;
    private readonly Relay stream;

    public RunRelayReportTests()
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

    private void Routes(Guid id, HttpStatusCode output = HttpStatusCode.NoContent)
    {
        fx.Fake.On($"POST /api/agent/runs/{id}/status", RemoteFixture.NoContent);
        fx.Fake.On($"POST /api/agent/runs/{id}/output", _ => (output, ""));
    }

    private static JsonElement Message(RunMessage run) => JsonSerializer.SerializeToElement(run, ApiClient.Json);

    private static RunMessage Shell(string command) =>
        new(Guid.NewGuid(), RunModes.Shell, null, command, null, 60, DateTimeOffset.UtcNow.AddMinutes(5), null, null);

    private Task<bool> Finished(Guid id) => ExecHarness.UntilAsync(() => fx.Store.ExecRunOf(id.ToString())?.State == LocalStore.ExecStates.Finished);

    [Fact]
    public async Task Output_refused_with_a_client_error_is_dropped_so_it_cannot_block_the_outcome_but_a_server_error_is_retried()
    {
        if (!ExecFixture.Unix) return;
        var run = Shell("echo refused-output");
        Routes(run.Id, HttpStatusCode.BadRequest);
        relay.Accept(run, CancellationToken.None);
        Assert.True(await Finished(run.Id));

        await relay.ReportAsync(CancellationToken.None);

        Assert.Empty(fx.Store.UnsentExecOutput(10)); // dropped, not kept for ever
        Assert.True(fx.Store.ExecRunOf(run.Id.ToString())!.Reported);
        Assert.Contains(fx.Fake.Seen, s => s.Path.EndsWith("/status", StringComparison.Ordinal) && s.Body.Contains("\"succeeded\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_outcome_refused_with_a_client_error_is_marked_reported_and_later_runs_are_still_reported()
    {
        if (!ExecFixture.Unix) return;
        var first = Shell("echo one");
        var second = Shell("echo two");
        Routes(second.Id);
        fx.Fake.On($"POST /api/agent/runs/{first.Id}/status", _ => (HttpStatusCode.Forbidden, "{}"));
        fx.Fake.On($"POST /api/agent/runs/{first.Id}/output", RemoteFixture.NoContent);
        relay.Accept(first, CancellationToken.None);
        relay.Accept(second, CancellationToken.None);
        Assert.True(await Finished(first.Id));
        Assert.True(await Finished(second.Id));

        await relay.ReportAsync(CancellationToken.None);

        Assert.True(fx.Store.ExecRunOf(first.Id.ToString())!.Reported);
        Assert.True(fx.Store.ExecRunOf(second.Id.ToString())!.Reported);
        Assert.Contains(fx.Fake.Seen, s => s.Path == $"/api/agent/runs/{second.Id}/status" && s.Body.Contains("\"succeeded\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_error_on_the_outcome_keeps_it_unreported_for_the_next_pass()
    {
        if (!ExecFixture.Unix) return;
        var run = Shell("echo three");
        fx.Fake.On($"POST /api/agent/runs/{run.Id}/output", RemoteFixture.NoContent);
        fx.Fake.On($"POST /api/agent/runs/{run.Id}/status", _ => (HttpStatusCode.InternalServerError, "{}"));
        relay.Accept(run, CancellationToken.None);
        Assert.True(await Finished(run.Id));

        await Assert.ThrowsAsync<ApiException>(() => relay.ReportAsync(CancellationToken.None));
        Assert.False(fx.Store.ExecRunOf(run.Id.ToString())!.Reported);
    }

    [Fact]
    public async Task Reporting_also_forgets_reported_runs_older_than_the_local_retention()
    {
        if (!ExecFixture.Unix) return;
        var old = Guid.NewGuid().ToString();
        fx.Store.ExecBegin(old, DateTimeOffset.UtcNow - fx.Home.Config.RemoteLocalRetention - TimeSpan.FromHours(1));
        fx.Store.ExecEnd(old, RunStatuses.Succeeded, 0, null, false, null);
        fx.Store.ExecReported(old);
        var recent = Guid.NewGuid().ToString();
        fx.Store.ExecBegin(recent, DateTimeOffset.UtcNow);
        fx.Store.ExecEnd(recent, RunStatuses.Succeeded, 0, null, false, null);
        fx.Store.ExecReported(recent);

        await relay.ReportAsync(CancellationToken.None);

        Assert.Null(fx.Store.ExecRunOf(old));
        Assert.NotNull(fx.Store.ExecRunOf(recent));
    }
}
