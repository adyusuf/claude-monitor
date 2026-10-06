using System.Collections.Concurrent;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The target's side of remote work (ADR-0005). A run arriving on the stream is written to exec_runs before anything
/// starts (a replay of a known run is never started again), executed by <see cref="RunExecutor"/>, and its output and
/// outcome go through the local database to the API, output first. A run that was alive when the daemon stopped is
/// reported failed, never restarted. The executor's callbacks come from other threads, so the store is used under a lock.
/// </summary>
public sealed class RunRelay(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock, RunExecutor executor,
    AgentLog log) : IDisposable
{
    public const string Restarted = "agent_restarted";
    private const int ChunksPerCall = 16;
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();

    /// <summary>Runs left "running" by a daemon that stopped: they are failed, their processes are gone with it.</summary>
    public void Recover()
    {
        lock (gate)
        {
            foreach (var r in store.ExecRunsIn(LocalStore.ExecStates.Running).Where(r => !running.ContainsKey(r.RunId)))
            {
                store.ExecEnd(r.RunId, RunStatuses.Failed, null, Restarted, r.Truncated, r.ResolvedExe);
            }
        }
    }

    /// <summary>Takes a run from the stream; it executes in the background. False when it was already known.</summary>
    public bool Accept(RunMessage run, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(run);
        var id = run.Id.ToString();
        lock (gate)
        {
            if (!store.ExecBegin(id, clock.GetUtcNow())) return false;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        running[id] = cts;
        _ = Task.Run(() => ExecuteAsync(run, cts), CancellationToken.None);
        return true;
    }

    public void Cancel(Guid runId)
    {
        if (running.TryGetValue(runId.ToString(), out var cts)) cts.Cancel();
    }

    /// <summary>Sends stored output, then the outcome of every finished run whose output is all sent.</summary>
    public async Task ReportAsync(CancellationToken ct)
    {
        List<StoredChunk> chunks;
        lock (gate) chunks = store.UnsentExecOutput(ChunksPerCall * 4);
        foreach (var group in chunks.GroupBy(c => c.RunId))
        {
            foreach (var part in group.Chunk(ChunksPerCall))
            {
                bool sent;
                try
                {
                    sent = await api.RunOutputAsync(Guid.Parse(group.Key),
                        part.Select(c => new RunOutputChunk(c.Seq, c.Stream, c.Body, c.Gap)).ToList(), ct);
                }
                catch (ApiException e) when (Permanent(e.Status))
                {
                    // refused for good: drop it so it cannot block later output
                    log.Write($"run {group.Key} output refused ({(int)e.Status}), dropped");
                    sent = false;
                }

                lock (gate)
                {
                    foreach (var c in part) store.ExecOutputSent(c.RunId, c.Seq); // sent, or the run is gone (404): either way done here
                }

                if (!sent) break;
            }
        }

        List<ExecRun> finished;
        lock (gate) finished = store.UnreportedExecs().Where(r => !store.HasUnsentOutput(r.RunId)).ToList();
        foreach (var r in finished)
        {
            try
            {
                await api.RunStatusAsync(Guid.Parse(r.RunId),
                    new RunStatusUpdate(r.FinalStatus ?? RunStatuses.Failed, r.ExitCode, r.Error, r.Truncated, r.ResolvedExe, clock.GetUtcNow()), ct);
            }
            catch (ApiException e) when (Permanent(e.Status))
            {
                log.Write($"run {r.RunId} outcome refused ({(int)e.Status}), dropped");
            }

            lock (gate) store.ExecReported(r.RunId);
        }

        lock (gate) store.ForgetRuns(clock.GetUtcNow() - config.RemoteLocalRetention);
    }

    private async Task ExecuteAsync(RunMessage run, CancellationTokenSource cts)
    {
        var id = run.Id.ToString();
        try
        {
            await Quietly(() => api.RunStatusAsync(run.Id, new RunStatusUpdate(RunStatuses.Delivered, At: clock.GetUtcNow()), cts.Token));
            var result = await executor.ExecuteAsync(run,
                chunk =>
                {
                    lock (gate) store.AddExecOutput(new StoredChunk(id, chunk.Seq, chunk.Stream, chunk.Body, chunk.GapBefore));
                    return Task.CompletedTask;
                },
                update => update.Status == RunStatuses.Running
                    ? Quietly(() => api.RunStatusAsync(run.Id, update, cts.Token))
                    : Task.CompletedTask,
                cts.Token);
            lock (gate) store.ExecEnd(id, result.Status, result.ExitCode, result.Error, result.OutputTruncated, result.ResolvedExe);
        }
        catch (Exception e)
        {
            log.Write($"run {id} failed in the agent: {e.GetType().Name}");
            lock (gate) store.ExecEnd(id, RunStatuses.Failed, null, RunFailures.SpawnFailed, false, null);
        }
        finally
        {
            running.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// An answer that will not change on a retry: the request itself is wrong or the run is settled or gone. 401, 403, 408
    /// and 429 (a token being renewed, a rate limit) are not: the report stays and goes again next pass.
    /// </summary>
    internal static bool Permanent(System.Net.HttpStatusCode status) => (int)status is 400 or 404 or 409 or 410 or 413 or 422;

    /// <summary>A live report that may fail (offline): the final outcome is stored and sent later anyway.</summary>
    private async Task Quietly(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or ApiException or OperationCanceledException)
        {
            log.Write($"run report deferred: {e.GetType().Name}");
        }
    }

    public void Dispose()
    {
        foreach (var cts in running.Values) cts.Cancel();
    }

    internal int Running => running.Count;
}
