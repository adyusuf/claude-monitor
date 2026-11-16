using System.Runtime.Versioning;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-323 on a real process: the executor reports the started process and the relay keeps what a restarted agent needs.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class RunRelayAdoptRealTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
    private readonly RemoteFixture fx = new();
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ap-adopt-" + Guid.NewGuid().ToString("N"));

    public RunRelayAdoptRealTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        fx.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task A_started_run_keeps_its_process_id_start_time_and_deadline_in_the_database_and_the_system_finds_the_same_process_again()
    {
        if (!ExecFixture.Unix) return;
        var log = new AgentLog(fx.Home.Config, TimeProvider.System);
        var executor = new RunExecutor(fx.Home.Config with { ExecKillGrace = TimeSpan.FromMilliseconds(300) }, TimeProvider.System, log,
            _ => new ExecDecision(true, null, "/bin/sh"), () => false);
        using var relay = new RunRelay(fx.Home.Config, fx.Store, fx.Api, TimeProvider.System, executor, log);
        var run = new RunMessage(Guid.NewGuid(), RunModes.Shell, null, "sleep 300", null, 120, DateTimeOffset.UtcNow.AddMinutes(5), null, null, null);
        fx.Fake.On($"POST /api/agent/runs/{run.Id}/status", RemoteFixture.NoContent);
        var before = DateTimeOffset.UtcNow;

        Assert.True(relay.Accept(run, CancellationToken.None));
        Assert.True(await ExecHarness.UntilAsync(() => fx.Store.ExecRunOf(run.Id.ToString())?.Pid is not null));

        var row = fx.Store.ExecRunOf(run.Id.ToString())!;
        Assert.True(row.Pid > 1);
        Assert.Equal(row.PidStart, new SystemRunAdoption().StartOf(row.Pid!.Value));
        Assert.InRange(row.Deadline!.Value - before, TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(125)); // the run's 120 s
        relay.Cancel(run.Id);
        Assert.True(await ExecHarness.UntilAsync(() => fx.Store.ExecRunOf(run.Id.ToString())!.State == LocalStore.ExecStates.Finished));
    }

    [Fact]
    public async Task The_system_adoption_reads_a_live_process_stops_its_group_and_knows_when_it_is_gone()
    {
        if (!ExecFixture.Unix) return;
        var system = new SystemRunAdoption();
        await using var process = ProcessTree.Start("/bin/sleep", ["300"], dir, Env);
        var pid = process.Pid!.Value;

        Assert.NotNull(system.StartOf(pid));
        system.Signal(pid, force: true);
        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(9, exit.Signal);
        Assert.Null(system.StartOf(pid));
        system.Signal(pid, force: true); // a group that is gone is not an error
    }

    [Fact]
    public void The_system_adoption_knows_no_process_with_an_unused_id_and_refuses_an_id_that_would_signal_everything()
    {
        if (!ExecFixture.Unix) return;
        var system = new SystemRunAdoption();

        Assert.Null(system.StartOf(int.MaxValue));
        Assert.NotNull(system.StartOf(Environment.ProcessId));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Signal(1, force: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => system.Signal(0, force: false));
    }
}
