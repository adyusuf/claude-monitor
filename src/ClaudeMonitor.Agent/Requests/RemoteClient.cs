using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Requests;

/// <summary>The result of a typed call: the value, or an error code. <see cref="Pending"/> means the daemon has not answered yet; the request stays queued and can be followed by its key.</summary>
public sealed record Outcome<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
    public bool Pending => Error == RemoteAnswer.Pending;
}

/// <summary>A followed run as the local database holds it: the row, the view the daemon fetched, the output so far and, for a script run, the verdict on its output.</summary>
public sealed record RunSnapshot(FollowedRun Run, RunView? View, IReadOnlyList<StoredChunk> Chunks, string? Completeness);

/// <summary>
/// The one requester layer of remote work (ADR-0008 §3g, ADR-0005): the typed calls an MCP tool or a command-line verb makes, over the local request rows the daemon
/// sends. It creates runs and asks, follows them in the local database (the daemon is the only API caller) and reads them back. The MCP tools only format and wrap what
/// comes back for a model; the CLI verbs print it for a person. A pending answer is not an error: the request stays queued, and its key follows it later.
/// </summary>
public sealed class RemoteClient(AgentConfig config, TimeProvider clock)
{
    private readonly RemoteRequests requests = new(config, clock);

    public RemoteRequests Requests => requests;

    public async Task<Outcome<List<MachineView>>> MachinesAsync() => Typed<List<MachineView>>(await requests.AskAsync("machines", HttpMethod.Get, "api/agent/machines", null, RemoteRequests.ReadWait), []);

    public async Task<Outcome<MachineDetail>> DetailAsync(Guid agentId) =>
        Typed<MachineDetail>(await requests.AskAsync("machine", HttpMethod.Get, $"api/agent/machines/{agentId}/detail", null, RemoteRequests.ReadWait));

    public async Task<Outcome<List<JobView>>> JobsAsync(Guid? target = null) =>
        Typed<List<JobView>>(await requests.AskAsync("jobs", HttpMethod.Get, $"api/agent/jobs{(target is { } t ? $"?target={t}" : "")}", null, RemoteRequests.ReadWait), []);

    /// <summary>Asks the API to create a run. <see cref="RunCreate.ClientKey"/> is the exactly-once key and the local request id, so a pending answer can be followed by it.</summary>
    public async Task<Outcome<RunCreated>> CreateRunAsync(RunCreate create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return Typed<RunCreated>(await requests.AskAsync(RemoteRelay.RunKind, HttpMethod.Post, "api/agent/runs", create, RemoteRequests.ReadWait, create.ClientKey));
    }

    public async Task<Outcome<AskCreated>> CreateAskAsync(AskCreate create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return Typed<AskCreated>(await requests.AskAsync(RemoteRelay.AskKind, HttpMethod.Post, "api/agent/asks", create, RemoteRequests.ReadWait, create.ClientKey));
    }

    /// <summary>The answer of an ask, once, from the session that received it. The daemon relays it; nothing local changes until the API accepted it.</summary>
    public async Task<Outcome<bool>> AnswerAsync(Guid askId, AskAnswer answer)
    {
        var result = await requests.AskAsync(RemoteRelay.AskKind + "-answer", HttpMethod.Post, $"api/agent/asks/{askId}/answer", answer, RemoteRequests.ReadWait);
        return new Outcome<bool>(result.Ok, result.Error);
    }

    /// <summary>A run id, or the key of the request that created it (when the call answered "pending"); null when this machine never asked for it.</summary>
    public async Task<string?> RunIdAsync(string runId)
    {
        if (!Guid.TryParse(runId, out var parsed)) return null;
        var key = parsed.ToString();
        using (var store = new LocalStore(config.DatabasePath))
        {
            if (store.Followed(key) is not null) return key;
            if (store.Request(key) is not { Kind: RemoteRelay.RunKind }) return null;
        }

        var answer = await requests.WaitAsync(key, RemoteRequests.ReadWait);
        return answer.Ok && answer.Json?.Deserialize<RunCreated>(ApiClient.Json) is { } created ? created.Id.ToString() : null;
    }

    /// <summary>An ask id, or the key of the request that created it; null when it is not an id and not a key this machine used.</summary>
    public async Task<Guid?> AskIdAsync(string askId)
    {
        if (!Guid.TryParse(askId, out var parsed)) return null;
        string? kind;
        using (var store = new LocalStore(config.DatabasePath)) kind = store.Request(parsed.ToString())?.Kind;
        if (kind != RemoteRelay.AskKind) return parsed;
        var answer = await requests.WaitAsync(parsed.ToString(), RemoteRequests.ReadWait);
        return answer.Ok && answer.Json?.Deserialize<AskCreated>(ApiClient.Json) is { } created ? created.Id : null;
    }

    /// <summary>
    /// The view of an ask, waiting up to <paramref name="waitSeconds"/> for it to end. The daemon keeps it fresh in the local database (the API's ask_update wakes it at once, a
    /// poll otherwise); an ask this machine does not follow yet is followed from here on. Never calls the API itself.
    /// </summary>
    public async Task<AskView?> WaitAskAsync(Guid id, int waitSeconds)
    {
        var key = id.ToString();
        var until = clock.GetUtcNow().AddSeconds(Math.Clamp(waitSeconds, 0, AgentConfig.RemoteWaitMaxSeconds));
        while (true)
        {
            FollowedAsk? row;
            using (var store = new LocalStore(config.DatabasePath))
            {
                row = store.AskOut(key);
                if (row is null)
                {
                    store.FollowAsk(key, AskStatuses.Queued, clock.GetUtcNow());
                }
                else if (!row.Done)
                {
                    store.AskChanged(key);
                }
            }

            if (row is { Done: true } || clock.GetUtcNow() >= until)
            {
                return row?.View is { } view ? JsonSerializer.Deserialize<AskView>(view, ApiClient.Json) : null;
            }

            await Task.Delay(config.RemoteWaitPoll > TimeSpan.FromSeconds(1) ? config.RemoteWaitPoll : TimeSpan.FromSeconds(1), clock);
        }
    }

    /// <summary>The run, its view and its output from the local database, waiting up to <paramref name="waitSeconds"/> for it to finish; null while the daemon has not heard of it.</summary>
    public async Task<RunSnapshot?> ReadRunAsync(string runId, int waitSeconds)
    {
        var until = clock.GetUtcNow().AddSeconds(Math.Clamp(waitSeconds, 0, AgentConfig.RemoteWaitMaxSeconds));
        FollowedRun? run;
        while (true)
        {
            using (var store = new LocalStore(config.DatabasePath))
            {
                run = store.Followed(runId);
                if (run is not null && !run.Done && run.Status != RunStatuses.PendingApproval) store.RunChanged(runId);
            }

            if ((run is not null && run.Done) || clock.GetUtcNow() >= until) break;
            await Task.Delay(TimeSpan.FromSeconds(1), clock);
        }

        if (run is null) return null;
        var view = run.View is { } v ? JsonSerializer.Deserialize<RunView>(v, ApiClient.Json) : null;
        using var s = new LocalStore(config.DatabasePath);
        var chunks = s.RunOutput(runId).ToList();
        return new RunSnapshot(run, view, chunks, run.Done ? ScriptResult.Completeness(view, chunks) : null);
    }

    private static Outcome<T> Typed<T>(RemoteAnswer answer, T? whenEmpty = default)
    {
        if (!answer.Ok) return new Outcome<T>(default, answer.Error);
        var value = answer.Json is { } json ? json.Deserialize<T>(ApiClient.Json) : default;
        return new Outcome<T>(value ?? whenEmpty, null);
    }
}
