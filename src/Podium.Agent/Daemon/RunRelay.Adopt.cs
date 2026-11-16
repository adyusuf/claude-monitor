using System.ComponentModel;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

/// <summary>
/// A run that outlived the agent that started it, picked up by the next one (F-323, ADR-0005). The old agent's pipes are gone, so the new one
/// cannot read the rest of the output and cannot learn the exit code: it keeps the run "running", enforces the run's original timeout, answers a
/// cancel, and when the process ends kills what is left of its group and reports the run failed with <see cref="RunFailures.ExitUnknown"/>. Windows runs never get here
/// (the Job Object ends them with the agent), and neither does a run the process table cannot vouch for.
/// </summary>
public sealed partial class RunRelay
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopStep = TimeSpan.FromMilliseconds(50);

    // The stored start time and the one read back come from the same call, so they match to the tick; a second is room for a clock step.
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    private TimeSpan Poll => adoptPoll ?? DefaultPoll;

    /// <summary>
    /// Runs left "running" by a daemon that stopped: one whose process is still the one that was started is picked up, any other is failed
    /// as <see cref="Restarted"/> (never started again either way).
    /// </summary>
    public void Recover()
    {
        List<ExecRun> left;
        lock (gate) left = store.ExecRunsIn(LocalStore.ExecStates.Running).Where(r => !running.ContainsKey(r.RunId)).ToList();
        foreach (var r in left)
        {
            if (TryPickUp(r)) continue;
            lock (gate) store.ExecEnd(r.RunId, RunStatuses.Failed, null, Restarted, r.Truncated, r.ResolvedExe);
        }
    }

    /// <summary>Keeps the started run's process in the database: a restarted agent finds the run through it.</summary>
    private void KeepProcess(string id, RunSpawn spawn)
    {
        var start = adopter.StartOf(spawn.Pid);
        if (start is null) return; // already gone: nothing to pick up
        lock (gate) store.ExecStarted(id, spawn.Pid, start.Value, clock.GetUtcNow() + spawn.Timeout);
    }

    private bool TryPickUp(ExecRun r)
    {
        // An id of 1 or less is never a run's group; kill(-1) would signal every process the agent may signal.
        if (r.Pid is not > 1 || r.PidStart is null || r.Deadline is null) return false;
        if (!Alive(r.RunId, r.Pid.Value, r.PidStart.Value)) return false;

        var cts = new CancellationTokenSource();
        if (!running.TryAdd(r.RunId, cts))
        {
            cts.Dispose();
            return true;
        }

        log.Write($"run {r.RunId} is still running after a restart: picked up");
        _ = Task.Run(() => WatchAsync(r, cts), CancellationToken.None);
        return true;
    }

    /// <summary>The stored process is still alive and is the one that was started. A table that cannot be read counts as alive: the deadline ends the run.</summary>
    private bool Alive(string runId, int pid, DateTimeOffset expectedStart)
    {
        try
        {
            return adopter.StartOf(pid) is { } start && (start - expectedStart).Duration() <= StartTolerance;
        }
        catch (Exception e) when (e is Win32Exception or NotSupportedException)
        {
            log.Write($"run {runId}: process table not readable ({e.GetType().Name})");
            return true;
        }
    }

    private async Task WatchAsync(ExecRun r, CancellationTokenSource cts)
    {
        try
        {
            var (status, error) = await WaitForEndAsync(r, cts);
            if (status is null) return; // the agent is stopping: the run stays as it is for the next one
            lock (gate) store.ExecEnd(r.RunId, status, null, error, r.Truncated, r.ResolvedExe);
        }
        catch (Exception e)
        {
            log.Write($"run {r.RunId} failed in the agent: {e.GetType().Name}");
            lock (gate) store.ExecEnd(r.RunId, RunStatuses.Failed, null, RunFailures.ExitUnknown, r.Truncated, r.ResolvedExe);
        }
        finally
        {
            running.TryRemove(r.RunId, out _);
            cts.Dispose();
        }
    }

    private async Task<(string? Status, string? Error)> WaitForEndAsync(ExecRun r, CancellationTokenSource cts)
    {
        var (pid, start, deadline) = (r.Pid!.Value, r.PidStart!.Value, r.Deadline!.Value);
        while (true)
        {
            if (cts.IsCancellationRequested)
            {
                if (disposing) return (null, null);
                await StopAsync(r.RunId, pid, start);
                return (RunStatuses.Cancelled, null);
            }

            if (clock.GetUtcNow() >= deadline)
            {
                await StopAsync(r.RunId, pid, start);
                return (RunStatuses.TimedOut, null);
            }

            if (!Alive(r.RunId, pid, start))
            {
                // The lead is gone but children it left in its group may not be: they must not outlive the run (the id was checked at the
                // pick-up, is above 1, and a group already gone is no error).
                TrySignal(r.RunId, pid, force: true);
                return (RunStatuses.Failed, RunFailures.ExitUnknown);
            }

            try
            {
                await Task.Delay(Poll, clock, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // the top of the loop reads the cancellation: a cancel or the agent stopping
            }
        }
    }

    // The polite stop, the grace period, then the hard one whether or not the lead left (its children may have ignored the first).
    private async Task StopAsync(string runId, int pid, DateTimeOffset start)
    {
        TrySignal(runId, pid, force: false);
        var grace = Task.Delay(config.ExecKillGrace, clock);
        while (!grace.IsCompleted && Alive(runId, pid, start)) await Task.Delay(StopStep, clock);
        TrySignal(runId, pid, force: true);
    }

    private void TrySignal(string runId, int pid, bool force)
    {
        try
        {
            adopter.Signal(pid, force);
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException)
        {
            log.Write($"run {runId}: stop failed ({e.GetType().Name})");
        }
    }
}
