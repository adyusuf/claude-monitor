using System.Collections.Concurrent;
using System.Net;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-323: a run that is still alive when the agent restarts is picked up by the next one, never failed as lost and never started again.</summary>
public sealed class RunRelayAdoptTests : IDisposable
{
    private const int FakePid = 4242;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
    private readonly RemoteFixture fx = new();
    private readonly FakeAdoption processes = new();
    private readonly string id = Guid.NewGuid().ToString();
    private readonly DateTimeOffset started;
    private int guardCalls;
    private RunRelay relay;

    public RunRelayAdoptTests()
    {
        started = fx.Clock.GetUtcNow().AddMinutes(-3);
        relay = Restarted();
    }

    public void Dispose()
    {
        relay.Dispose();
        fx.Dispose();
    }

    /// <summary>The next daemon: a new relay over the same database.</summary>
    private RunRelay Restarted()
    {
        var log = new AgentLog(fx.Home.Config, TimeProvider.System);
        var executor = new RunExecutor(fx.Home.Config, fx.Clock, log, _ =>
        {
            Interlocked.Increment(ref guardCalls);
            return new ExecDecision(true, null, "/bin/sh");
        }, () => false);
        return new RunRelay(fx.Home.Config with { ExecKillGrace = TimeSpan.FromMilliseconds(100) }, fx.Store, fx.Api, fx.Clock, executor, log, processes, Poll);
    }

    /// <summary>The state a crash leaves: the run row says running, with the process the old agent recorded.</summary>
    private void Left(int pid = FakePid, TimeSpan? timeout = null)
    {
        fx.Store.ExecBegin(id, started);
        fx.Store.ExecStarted(id, pid, started, started + (timeout ?? TimeSpan.FromMinutes(30)));
        processes.Alive[pid] = started;
    }

    private ExecRun Row() => fx.Store.ExecRunOf(id)!;

    private Task<bool> Finished() => ExecHarness.UntilAsync(() => Row().State == LocalStore.ExecStates.Finished);

    [Fact]
    public async Task A_run_still_alive_after_a_restart_stays_running_is_not_started_again_and_is_closed_when_its_process_ends()
    {
        Left();

        relay.Recover();

        Assert.Equal((LocalStore.ExecStates.Running, null, null), (Row().State, Row().FinalStatus, Row().Error));
        Assert.Equal(1, relay.Running);
        Assert.Empty(processes.Signals);
        var again = new RunMessage(Guid.Parse(id), RunModes.Shell, null, "echo twice", null, 60, fx.Clock.GetUtcNow().AddMinutes(5), null, null, null);
        Assert.False(relay.Accept(again, CancellationToken.None)); // the replay is not a new run
        Assert.Equal(0, Volatile.Read(ref guardCalls));

        processes.Alive.TryRemove(FakePid, out _); // the run ends while nobody is reading its pipes: its exit code is not known
        Assert.True(await Finished());
        Assert.Equal((RunStatuses.Failed, RunFailures.ExitUnknown), (Row().FinalStatus, Row().Error));
        Assert.True(await ExecHarness.UntilAsync(() => relay.Running == 0));
    }

    [Fact]
    public void A_process_that_is_not_the_one_that_was_started_is_not_picked_up_the_run_is_failed_as_restarted()
    {
        Left();
        processes.Alive[FakePid] = started.AddHours(5); // the id was recycled by another program

        relay.Recover();

        Assert.Equal((LocalStore.ExecStates.Finished, RunStatuses.Failed, RunRelay.Restarted), (Row().State, Row().FinalStatus, Row().Error));
        Assert.Empty(processes.Signals); // nothing that is not ours is ever signalled
        Assert.Equal(0, relay.Running);
    }

    [Fact]
    public void A_process_that_is_gone_or_a_row_from_before_the_process_was_kept_is_failed_as_restarted()
    {
        Left();
        processes.Alive.Clear();
        var old = Guid.NewGuid().ToString();
        fx.Store.ExecBegin(old, started); // no process id, as every row of an older agent

        relay.Recover();

        Assert.Equal(RunRelay.Restarted, Row().Error);
        Assert.Equal((RunStatuses.Failed, RunRelay.Restarted), (fx.Store.ExecRunOf(old)!.FinalStatus, fx.Store.ExecRunOf(old)!.Error));
    }

    [Fact]
    public void A_stored_id_of_one_or_less_is_never_picked_up_so_the_kill_cannot_reach_every_process()
    {
        Left(pid: 1);

        relay.Recover();

        Assert.Equal(RunRelay.Restarted, Row().Error);
        Assert.Empty(processes.Signals);
    }

    [Fact]
    public async Task Cancelling_a_picked_up_run_stops_its_process_group_politely_then_hard_and_ends_it_cancelled()
    {
        Left();
        relay.Recover();

        relay.Cancel(Guid.Parse(id));

        Assert.True(await Finished());
        Assert.Equal(RunStatuses.Cancelled, Row().FinalStatus);
        Assert.Equal([(FakePid, false), (FakePid, true)], processes.Signals.ToArray()); // the polite stop, then the hard one for what the lead's children ignored
    }

    [Fact]
    public async Task A_picked_up_run_that_ignores_the_polite_stop_is_killed_after_the_grace_period()
    {
        processes.TermEnds = false;
        Left();
        relay.Recover();

        relay.Cancel(Guid.Parse(id));

        Assert.True(await Finished());
        Assert.Equal([(FakePid, false), (FakePid, true)], processes.Signals.ToArray());
        Assert.Equal(RunStatuses.Cancelled, Row().FinalStatus);
    }

    [Fact]
    public async Task A_picked_up_run_is_stopped_as_timed_out_when_its_original_timeout_has_passed()
    {
        Left(timeout: TimeSpan.FromMinutes(10));
        relay.Recover();
        Assert.Equal(LocalStore.ExecStates.Running, Row().State);

        fx.Clock.Advance(TimeSpan.FromMinutes(8)); // the run started 3 minutes ago with 10 minutes to go: 7 remain, 8 pass
        Assert.True(await Finished());

        Assert.Equal(RunStatuses.TimedOut, Row().FinalStatus);
        Assert.Contains((FakePid, false), processes.Signals);
    }

    [Fact]
    public async Task Stopping_the_agent_does_not_stop_or_end_a_picked_up_run_the_next_start_picks_it_up_again()
    {
        Left();
        relay.Recover();

        relay.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // longer than a few polls
        Assert.Equal(LocalStore.ExecStates.Running, Row().State);
        Assert.Empty(processes.Signals);

        relay = Restarted();
        relay.Recover();
        Assert.Equal(1, relay.Running);
        Assert.Equal(LocalStore.ExecStates.Running, Row().State);
    }

    [Fact]
    public async Task A_picked_up_runs_outcome_is_sent_to_the_api_like_any_other()
    {
        fx.Fake.On($"POST /api/agent/runs/{id}/status", RemoteFixture.NoContent);
        fx.Fake.On($"POST /api/agent/runs/{id}/output", _ => (HttpStatusCode.NoContent, ""));
        Left();
        relay.Recover();
        processes.Alive.Clear();
        Assert.True(await Finished());

        await relay.ReportAsync(CancellationToken.None);

        Assert.Contains(fx.Fake.Seen, s => s.Path.EndsWith($"{id}/status", StringComparison.Ordinal) && s.Body.Contains(RunFailures.ExitUnknown, StringComparison.Ordinal));
        Assert.True(Row().Reported);
    }

    [Fact]
    public async Task When_the_lead_of_a_picked_up_run_ends_its_group_is_killed_once_so_no_child_outlives_the_run()
    {
        Left();
        relay.Recover();

        processes.Alive.TryRemove(FakePid, out _); // the lead is gone; children it left in the group are not in the table
        Assert.True(await Finished());

        Assert.Equal((RunStatuses.Failed, RunFailures.ExitUnknown), (Row().FinalStatus, Row().Error));
        Assert.Equal([(FakePid, true)], processes.Signals.ToArray());
    }

    [Fact]
    public async Task A_group_kill_that_fails_after_the_lead_ended_is_logged_and_the_run_still_ends()
    {
        processes.SignalError = new IOException("kill failed (errno 1)");
        Left();
        relay.Recover();

        processes.Alive.TryRemove(FakePid, out _);
        Assert.True(await Finished());

        Assert.Equal((RunStatuses.Failed, RunFailures.ExitUnknown), (Row().FinalStatus, Row().Error));
        Assert.Equal([(FakePid, true)], processes.Signals.ToArray());
    }

    private sealed class FakeAdoption : IRunAdoption
    {
        public ConcurrentDictionary<int, DateTimeOffset> Alive { get; } = new();
        public ConcurrentQueue<(int Pid, bool Force)> Signals { get; } = new();

        /// <summary>Whether the polite stop ends the process (false: only the hard one does).</summary>
        public bool TermEnds { get; set; } = true;

        /// <summary>What a signal fails with after it is recorded (null: it does not fail; a group already gone is no failure, as ESRCH).</summary>
        public Exception? SignalError { get; set; }

        public DateTimeOffset? StartOf(int pid) => Alive.TryGetValue(pid, out var at) ? at : null;

        public void Signal(int pid, bool force)
        {
            Signals.Enqueue((pid, force));
            if (force || TermEnds) Alive.TryRemove(pid, out _);
            if (SignalError is { } error) throw error;
        }
    }
}
