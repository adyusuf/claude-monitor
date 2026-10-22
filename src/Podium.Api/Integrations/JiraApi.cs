using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Podium.Api.Config;

namespace Podium.Api.Integrations;

/// <summary>
/// The HTTP primitive for one Jira site: the base address is built here from the API base and the cloud id (never from text a person
/// typed), a 429 is waited out a few times, and a failure becomes a <see cref="TrackerException"/> with a short code. A response body
/// is never put in an exception or a log: it is the tool's text and may hold personal data.
/// </summary>
public sealed class JiraApi(IHttpClientFactory clients, ApiConfig config)
{
    public const string ClientName = "atlassian";
    private const int Attempts = 3;
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

                if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
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
