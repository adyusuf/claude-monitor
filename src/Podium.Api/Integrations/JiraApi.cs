using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Podium.Api.Config;

namespace Podium.Api.Integrations;

/// <summary>
/// The HTTP primitive for one Jira site: the base address is built here from the API base and the cloud id (never from text a person
/// typed), a 429 is waited out a few times, and a failure becomes a <see cref="TrackerException"/> with a short code. A response body
/// is never put in an exception or a log: it is the tool's text and may hold personal data. The one exception is the gateway's own
/// refusal (401), whose short fixed message and WWW-Authenticate challenge say WHY the token was refused (a missing scope, an expired
/// token); that, the method, the path without its query and the status are logged when Jira refuses a call (401, 403) or fails (5xx).
/// </summary>
public sealed class JiraApi(IHttpClientFactory clients, ApiConfig config, ILogger<JiraApi> log)
{
    public const string ClientName = "atlassian";
    private const int Attempts = 3;
    private const int ReasonMax = 160;
    private const int GatewayBodyMax = 2048;
    private static readonly TimeSpan RetryCap = TimeSpan.FromSeconds(20);

    public Task<JsonDocument> GetAsync(TrackerContext ctx, string path, CancellationToken ct) => SendAsync(ctx, HttpMethod.Get, path, null, ct);

    public Task<JsonDocument> PostAsync(TrackerContext ctx, string path, object body, CancellationToken ct) =>
        SendAsync(ctx, HttpMethod.Post, path, body, ct);

    public Task<JsonDocument> PutAsync(TrackerContext ctx, string path, object body, CancellationToken ct) =>
        SendAsync(ctx, HttpMethod.Put, path, body, ct);

    public async Task<JsonDocument> SendAsync(TrackerContext ctx, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var url = $"{config.AtlassianApiBase}/ex/jira/{Uri.EscapeDataString(ctx.SiteId)}{path}";
        var json = body is null ? null : JsonSerializer.Serialize(body);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ctx.AccessToken(ct));
            request.Headers.Accept.ParseAdd("application/json");
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException)
            {
                throw new TrackerException(TrackerException.Codes.Unavailable);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TrackerException(TrackerException.Codes.Unavailable);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < Attempts)
                {
                    await Task.Delay(RetryAfter(response), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    await LogRefusalAsync(method, path, response, ct);
                    throw Failure(response.StatusCode);
                }

                if (response.StatusCode == HttpStatusCode.NoContent) return JsonDocument.Parse("{}");
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                try
                {
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (JsonException)
                {
                    throw new TrackerException(TrackerException.Codes.Unavailable);
                }
            }
        }
    }

    /// <summary>
    /// Says which call Jira refused and why, so a permission problem can be told from an expired token without guessing. Only 401 and 403 (and
    /// 5xx) are logged: a 404 or a 400 is an ordinary answer the callers expect. The path has no query and no cloud id; a token is never in it.
    /// </summary>
    private async Task LogRefusalAsync(HttpMethod method, string path, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var route = path.Split('?', 2)[0];
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                var reason = ChallengeOf(response) ?? (response.StatusCode == HttpStatusCode.Unauthorized ? await GatewayMessageAsync(response, ct) : null);
                log.LogWarning("Jira refused {Method} {Route}: {Status} {Reason}", method.Method, route, status, reason ?? "no reason given");
                break;
            case >= HttpStatusCode.InternalServerError:
                log.LogWarning("Jira failed {Method} {Route}: {Status}", method.Method, route, status);
                break;
            default:
                break;
        }
    }

    /// <summary>The WWW-Authenticate challenge ("Bearer ... error=insufficient_scope"): standard, no personal data, no token.</summary>
    private static string? ChallengeOf(HttpResponseMessage response)
    {
        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
        if (challenge is null) return null;
        return Short($"{challenge.Scheme} {challenge.Parameter}".Trim());
    }

    /// <summary>The gateway's own 401 body, {"code":401,"message":"Unauthorized; scope does not match"}; anything else is not read into a log.</summary>
    private static async Task<string?> GatewayMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > GatewayBodyMax) return null;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number == 401
                   && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? Short(message.GetString()!)
                : null;
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>One line, no control characters, at most <see cref="ReasonMax"/> characters.</summary>
    private static string Short(string text)
    {
        var clean = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return clean.Length <= ReasonMax ? clean : clean[..ReasonMax] + "...";
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
        return wait > RetryCap ? RetryCap : wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static TrackerException Failure(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => TrackerException.Codes.Unauthorized,
        HttpStatusCode.Forbidden => TrackerException.Codes.Forbidden,
        HttpStatusCode.NotFound => TrackerException.Codes.NotFound,
        HttpStatusCode.TooManyRequests => TrackerException.Codes.RateLimited,
        HttpStatusCode.Conflict => TrackerException.Codes.Changed,
        HttpStatusCode.BadRequest => TrackerException.Codes.Rejected,
        _ => TrackerException.Codes.Unavailable,
    });
}
