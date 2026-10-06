using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// Stands in for the daemon next to an MCP tool: it takes the requests the tool wrote into the local database and answers
/// them from a table, exactly as the real relay does after calling the API. Requests it has no answer for stay new.
/// </summary>
public sealed class RemoteFakeDaemon : IDisposable
{
    private readonly TempHome home;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly Dictionary<string, Func<RemoteRequest, (string State, string? Result, string? Error)>> answers = new();

    public RemoteFakeDaemon(TempHome home)
    {
        this.home = home;
        loop = Task.Run(Loop);
    }

    public ConcurrentQueue<RemoteRequest> Seen { get; } = new();

    public IEnumerable<RemoteRequest> OfKind(string kind) => Seen.Where(r => r.Kind == kind);

    public RemoteFakeDaemon Answer(string kind, object? result)
    {
        answers[kind] = _ => (LocalStore.RequestStates.Done, result is null ? null : result as string ?? JsonSerializer.Serialize(result, ApiClient.Json), null);
        return this;
    }

    public RemoteFakeDaemon Fail(string kind, string error)
    {
        answers[kind] = _ => (LocalStore.RequestStates.Failed, null, error);
        return this;
    }

    public RemoteFakeDaemon Machines(params MachineView[] machines) => Answer("machines", machines);

    private async Task Loop()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        while (!stop.IsCancellationRequested)
        {
            foreach (var r in store.NewRequests(20))
            {
                Seen.Enqueue(r);
                if (!answers.TryGetValue(r.Kind, out var answer)) continue;
                var (state, result, error) = answer(r);
                store.RequestAnswered(r.LocalId, state, result, error, DateTimeOffset.UtcNow);
            }

            await Task.Delay(5);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // the loop ended by cancellation
        }

        stop.Dispose();
    }

    public static MachineView Machine(string host, string os = OsKinds.Linux, string user = "ops", bool service = false, string exec = ExecLevels.Argv, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), host, os, user, exec, service, true, DateTimeOffset.UtcNow, null, 0);

    public static TempHome QuickHome() => new(c => c with { RemoteWaitPoll = TimeSpan.FromMilliseconds(5) });
}
