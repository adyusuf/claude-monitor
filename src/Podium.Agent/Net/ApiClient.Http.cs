using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Podium.Agent.Auth;
using Podium.Contracts;

namespace Podium.Agent.Net;

/// <summary>The plumbing under every call: the per-call deadline, the one refresh on a 401, reading and checking an answer.</summary>
public sealed partial class ApiClient
{
    private async Task<string?> ReadLineWithinAsync(StreamReader reader, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(StreamIdleTimeout);
        try
        {
            return await reader.ReadLineAsync(idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"the stream was silent for {StreamIdleTimeout.TotalSeconds:0} s");
        }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one call under its own deadline. A request on a dead pooled connection would otherwise wait for the OS to give
    /// up on TCP. The deadline surfaces as a <see cref="TimeoutException"/>, never as a cancellation: the daemon's loops
    /// read a cancellation as shutdown and a timeout as a failure to retry. The caller's own cancellation stays one.
    /// </summary>
    private async Task<T> WithinDeadlineAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(callTimeout);
        try
        {
            return await call(deadline.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"the API did not answer within {callTimeout.TotalSeconds:0.#} s"), e);
        }
    }

    private async Task WithinDeadlineAsync(Func<CancellationToken, Task> call, CancellationToken ct) =>
        await WithinDeadlineAsync(async t =>
        {
            await call(t);
            return true;
        }, ct);

    private static HttpRequestMessage Json_<T>(HttpMethod method, string url, T body) =>
        new(method, url) { Content = JsonContent.Create(body, options: Json) };

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var response = await SendOnceAsync(build, completion, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !await RefreshAsync(ct)) return response;
        response.Dispose();
        return await SendOnceAsync(build, completion, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> build, HttpCompletionOption completion, CancellationToken ct)
    {
        using var request = build();
        if (credentials.Read(Credentials.Access) is { } access) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await http.SendAsync(request, completion, ct);
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await refreshing.WaitAsync(ct);
        try
        {
            if (credentials.Read(Credentials.Refresh) is not { } refresh) return Fail(NoRefreshToken);
            using var response = await http.PostAsJsonAsync("api/agent/token/refresh", new RefreshRequest(refresh), Json, ct);
            if (!response.IsSuccessStatusCode) return Fail(await RefusalReasonAsync(response, ct));
            SaveTokens((await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct))!);
            return true;
        }
        finally
        {
            refreshing.Release();
        }

        bool Fail(string reason)
        {
            Disconnected = true;
            DisconnectReason = reason;
            return false;
        }
    }

    /// <summary>The <c>reason</c> of the 401 problem document; an older server sends none, so the status code stands in. Only the closed set of codes is kept.</summary>
    private static async Task<string> RefusalReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = $"http_{(int)response.StatusCode}";
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String
                   && reason.GetString() is { Length: <= 40 } code && code.All(c => char.IsAsciiLetterLower(c) || c == '_')
                ? code
                : fallback;
        }
        catch (System.Text.Json.JsonException)
        {
            return fallback; // not a problem document (a proxy's page, an empty body)
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            await EnsureAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ApiException(response.StatusCode, body.Length > 300 ? body[..300] : body);
    }
}
