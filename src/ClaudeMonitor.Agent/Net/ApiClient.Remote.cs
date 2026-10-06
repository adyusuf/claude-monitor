using System.Net;
using System.Text.Json;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Net;

// Remote work (ADR-0004): the calls of a target (profile, metrics, alerts, its runs' status and output) and a requester's
// calls, which the daemon relays for the MCP tools as raw JSON.
public sealed partial class ApiClient
{
    public Task ProfileAsync(AgentProfile profile, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Put, "api/agent/profile", profile), t);
        await EnsureAsync(response, t);
    }, ct);

    public Task MetricsAsync(MetricsReport report, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, "api/agent/metrics", report), t);
        await EnsureAsync(response, t);
    }, ct);

    public Task AlertsAsync(IReadOnlyList<AlertReport> reports, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, "api/agent/alerts", reports), t);
        await EnsureAsync(response, t);
    }, ct);

    /// <summary>A target's report on a run; false when the API says it is settled or gone (409/404), which ends the reporting.</summary>
    public Task<bool> RunStatusAsync(Guid id, RunStatusUpdate update, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, $"api/agent/runs/{id}/status", update), t);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        await EnsureAsync(response, t);
        return true;
    }, ct);

    public Task<bool> RunOutputAsync(Guid id, IReadOnlyList<RunOutputChunk> chunks, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, $"api/agent/runs/{id}/output", chunks), t);
        if (response.StatusCode is HttpStatusCode.NotFound) return false;
        await EnsureAsync(response, t);
        return true;
    }, ct);

    /// <summary>A requester's call relayed for an MCP tool: the answer's JSON, or an <see cref="ApiException"/>.</summary>
    public Task<JsonElement> RemoteAsync(HttpMethod method, string url, JsonElement? body, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => body is { } b
            ? Json_(method, url, b)
            : new HttpRequestMessage(method, url), t);
        await EnsureAsync(response, t);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return default;
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(t), cancellationToken: t);
        return doc.RootElement.Clone();
    }, ct);
}
