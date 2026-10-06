using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>The answer to a relayed call: the API's JSON, or an error code (an API error, "offline", or "pending" while it waits).</summary>
public sealed record RemoteAnswer(JsonElement? Json, string? Error)
{
    public const string Pending = "pending";
    public bool Ok => Error is null;
}

/// <summary>
/// How an MCP tool talks to the API without calling it (ADR-0004): it writes a request for the daemon into the local
/// database and waits a little for the answer. The daemon is the only API caller (refresh tokens are single-use).
/// </summary>
public sealed class RemoteRequests(AgentConfig config, TimeProvider clock)
{
    /// <summary>How long a read waits for the daemon: it relays every RemotePollEvery.</summary>
    public static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(15);

    public async Task<RemoteAnswer> AskAsync(string kind, HttpMethod method, string url, object? body, TimeSpan wait,
        string? localId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        localId ??= Guid.NewGuid().ToString();
        using (var store = new LocalStore(config.DatabasePath))
        {
            store.AddRequest(localId, kind, method.Method, url, body is null ? null : JsonSerializer.Serialize(body, ApiClient.Json),
                clock.GetUtcNow());
        }

        return await WaitAsync(localId, wait, ct);
    }

    public async Task<RemoteAnswer> WaitAsync(string localId, TimeSpan wait, CancellationToken ct = default)
    {
        var until = clock.GetUtcNow() + wait;
        while (true)
        {
            using (var store = new LocalStore(config.DatabasePath))
            {
                var r = store.Request(localId);
                if (r is null) return new RemoteAnswer(null, "not_found");
                if (r.State == LocalStore.RequestStates.Done)
                {
                    if (r.Result is null) return new RemoteAnswer(null, null);
                    using var doc = JsonDocument.Parse(r.Result);
                    return new RemoteAnswer(doc.RootElement.Clone(), null);
                }

                if (r.State == LocalStore.RequestStates.Failed) return new RemoteAnswer(null, r.Error ?? "failed");
            }

            if (clock.GetUtcNow() >= until) return new RemoteAnswer(null, RemoteAnswer.Pending);
            await Task.Delay(config.RemoteWaitPoll, clock, ct);
        }
    }
}
