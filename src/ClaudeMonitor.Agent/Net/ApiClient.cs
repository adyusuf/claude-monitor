using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Net;

/// <summary>
/// The agent's calls to the API. A 401 is answered once with a token refresh and a retry. Every authenticated call
/// except the stream gives up after <c>callTimeout</c>, refresh included, with a <see cref="TimeoutException"/>.
/// </summary>
public sealed partial class ApiClient(HttpClient http, ICredentialStore credentials, TimeSpan callTimeout) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim refreshing = new(1, 1);

    /// <summary>A stream that stays silent this long is dead (the API pings every 20 s): reading it fails with a <see cref="TimeoutException"/>.</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>Set when the API refused the refresh token: the agent must be connected again.</summary>
    public bool Disconnected { get; private set; }

    public void Dispose() => refreshing.Dispose();

    /// <summary>
    /// The API's client. No timeout unless one is given: the daemon's stream stays open, and its other calls are bounded
    /// by the per-call deadline instead.
    /// </summary>
    public static HttpClient CreateHttp(string server, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var client = handler is null ? new HttpClient(CreateHandler()) : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(server.TrimEnd('/') + "/");
        client.Timeout = timeout ?? Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Add(AgentHeaders.Version, AgentConfig.Version);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"cm-agent/{AgentConfig.Version}");
        return client;
    }

    /// <summary>
    /// Pooled connections are retired, so a socket opened before a network change (new Wi-Fi, VPN up or down) is not
    /// reused for ever: on macOS such a socket fails with EADDRNOTAVAIL, or a read on it waits until TCP gives up.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = AgentConfig.ConnectionLifetime,
        PooledConnectionIdleTimeout = AgentConfig.ConnectionIdle,
        ConnectTimeout = AgentConfig.ConnectTimeout,
        ConnectCallback = HappyEyeballs.ConnectAsync,
    };

    public void SaveTokens(TokenResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        credentials.Write(Credentials.Access, tokens.AccessToken);
        credentials.Write(Credentials.Refresh, tokens.RefreshToken);
        Disconnected = false;
    }

    // ---- device flow (no token yet) ---------------------------------------------------------------------------

    public async Task<DeviceCodeResponse> DeviceCodeAsync(DeviceCodeRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/device/code", request, Json, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(Json, ct))!;
    }

    /// <summary>The tokens, or the RFC 8628 error code while there are none yet.</summary>
    public async Task<(TokenResponse? Tokens, string? Error)> DeviceTokenAsync(string deviceCode, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/device/token", new DeviceTokenRequest(deviceCode), Json, ct);
        if (response.IsSuccessStatusCode) return ((await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct))!, null);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return (null, (await response.Content.ReadFromJsonAsync<DeviceTokenError>(Json, ct))?.Error ?? DeviceTokenErrors.Expired);
        }

        await EnsureAsync(response, ct);
        return (null, DeviceTokenErrors.Expired);
    }

    // ---- authenticated calls -----------------------------------------------------------------------------------

    public Task<BatchAck> SendBatchAsync(EventBatch batch, CancellationToken ct) => WithinDeadlineAsync(async t =>
        await ReadAsync<BatchAck>(await SendAsync(() => Json_(HttpMethod.Post, "api/agent/batches", batch), t), t), ct);

    public Task HeartbeatAsync(CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/agent/heartbeat"), t);
        await EnsureAsync(response, t);
    }, ct);

    public Task<AgentSettings> SettingsAsync(CancellationToken ct) => WithinDeadlineAsync(async t =>
        await ReadAsync<AgentSettings>(await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/agent/settings"), t), t), ct);

    /// <summary>The newest signed build the server hands out for this OS and CPU; null when none is published. The caller verifies it.</summary>
    public Task<UpdateOffer?> LatestAsync(string os, string arch, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        var url = $"api/agent/latest?os={Uri.EscapeDataString(os)}&arch={Uri.EscapeDataString(arch)}";
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), t);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        return await ReadAsync<UpdateOffer>(response, t);
    }, ct);

    public Task CommandStatusAsync(string id, string status, string? result, CancellationToken ct, DateTimeOffset? at = null) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, $"api/agent/commands/{id}/status", new CommandStatusUpdate(status, result, at)), t);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return; // already settled or gone
        await EnsureAsync(response, t);
    }, ct);

    public Task<PermissionRequestCreated?> CreatePermissionAsync(PermissionRequestCreate request, CancellationToken ct) => WithinDeadlineAsync(async t =>
    {
        using var response = await SendAsync(() => Json_(HttpMethod.Post, "api/agent/permission-requests", request), t);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // the session has not reached the API yet
        return await ReadAsync<PermissionRequestCreated>(response, t);
    }, ct);

    /// <summary>
    /// The agent's stream: (event name, data) pairs until the server or the token closes it. <paramref name="onOpen"/> runs
    /// once the API has accepted the connection (the first pings come only after 20 s).
    /// </summary>
    public async IAsyncEnumerable<(string Event, JsonElement Data)> StreamAsync([EnumeratorCancellation] CancellationToken ct, Action? onOpen = null)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/agent/stream"), ct,
            HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(response, ct);
        onOpen?.Invoke();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        string? name = null;
        while (await ReadLineWithinAsync(reader, ct) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal) && name is not null)
            {
                using var doc = JsonDocument.Parse(line[5..].Trim());
                yield return (name, doc.RootElement.Clone());
                name = null;
            }
        }
    }
}

public sealed class ApiException(HttpStatusCode status, string body) : Exception($"API answered {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The answer's body (a problem document's title is the error code the MCP tools show).</summary>
    public string Body { get; } = body;
}
