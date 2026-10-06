using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The requester's side of remote work (ADR-0004): the MCP tools write a request into the local database, this sends it
/// and writes the answer back; runs it created are followed (on run_update at once, otherwise every
/// <see cref="AgentConfig.RemotePollEvery"/>) until they finish, with their output. The MCP process never calls the API.
/// </summary>
public sealed class RemoteRelay(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock)
{
    public const string RunKind = "run";
    public const string Unavailable = "remote_unavailable";
    public const string Offline = "offline";
    public const string BadAnswer = "bad_answer";
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);
    private const int OutputPage = 50;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await SendRequestsAsync(ct);
        await FollowRunsAsync(ct);
        store.ForgetRequests(clock.GetUtcNow() - ForgetAfter);
        store.ForgetRuns(clock.GetUtcNow() - config.RemoteLocalRetention);
    }

    public async Task SendRequestsAsync(CancellationToken ct)
    {
        foreach (var r in store.NewRequests(20))
        {
            var now = clock.GetUtcNow();
            try
            {
                using var body = r.Body is null ? null : JsonDocument.Parse(r.Body);
                var answer = await api.RemoteAsync(new HttpMethod(r.Method), r.Url, body?.RootElement, ct);
                var json = answer.ValueKind == JsonValueKind.Undefined ? null : answer.GetRawText();
                store.RequestAnswered(r.LocalId, LocalStore.RequestStates.Done, json, null, now);
                if (r.Kind == RunKind && json is not null && JsonSerializer.Deserialize<RunCreated>(json, ApiClient.Json) is { } created)
                {
                    store.FollowRun(created.Id.ToString(), created.Status, now);
                }
            }
            catch (ApiException e)
            {
                store.RequestAnswered(r.LocalId, LocalStore.RequestStates.Failed, null, ErrorCode(e), now);
            }
            catch (JsonException)
            {
                // an answer that is not JSON (a proxy's page) must not block the requests behind it
                store.RequestAnswered(r.LocalId, LocalStore.RequestStates.Failed, null, BadAnswer, now);
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException && !ct.IsCancellationRequested)
            {
                if (now - r.CreatedAt > GiveUpAfter) store.RequestAnswered(r.LocalId, LocalStore.RequestStates.Failed, null, Offline, now);
                throw; // the loop backs off; the request stays new until it is old
            }
        }
    }

    public async Task FollowRunsAsync(CancellationToken ct)
    {
        foreach (var run in store.RunsToFetch(clock.GetUtcNow() - config.RemotePollEvery, 20))
        {
            try
            {
                var view = await api.RemoteAsync(HttpMethod.Get, $"api/agent/runs/{run.RunId}", null, ct);
                var status = view.GetProperty("status").GetString() ?? run.Status;
                var next = run.NextSeq;
                var outputDone = false;
                for (var page = 0; page < 20; page++)
                {
                    var answer = await api.RemoteAsync(HttpMethod.Get, $"api/agent/runs/{run.RunId}/output?after={next}&limit={OutputPage}", null, ct);
                    var chunks = answer.Deserialize<RunOutputPage>(ApiClient.Json);
                    if (chunks is null) break;
                    store.AddRunOutput(run.RunId, chunks.Chunks.Select(c => new StoredChunk(run.RunId, c.Seq, c.Stream, c.Body, c.GapBefore)));
                    next = chunks.NextSeq;
                    outputDone = chunks.Done;
                    if (chunks.Chunks.Count < OutputPage) break;
                }

                store.RunFetched(run.RunId, status, view.GetRawText(), next, RunStatuses.Final.Contains(status) && outputDone, clock.GetUtcNow());
            }
            catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
            {
                store.RunFetched(run.RunId, run.Status, run.View ?? "{}", run.NextSeq, true, clock.GetUtcNow());
            }
        }
    }

    /// <summary>The problem's title when the API sent one (an error code such as target_cannot_run), else the status code.</summary>
    public static string ErrorCode(ApiException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Status == HttpStatusCode.NotFound && e.Body.Length == 0) return Unavailable;
        try
        {
            using var doc = JsonDocument.Parse(e.Body);
            if (doc.RootElement.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 and <= 100 } code) return code;
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0) return field.Value[0].GetString() ?? "invalid";
                }
            }
        }
        catch (JsonException)
        {
            // not a problem document
        }

        return e.Status == HttpStatusCode.NotFound ? "not_found" : ((int)e.Status).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
