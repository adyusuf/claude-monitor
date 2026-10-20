using System.Net;
using System.Text;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The HTTP primitive for one Jira site (ADR-0015 §3): the address, the wait on a 429, and failures that carry a code and never the response text.</summary>
public sealed class JiraApiTests
{
    private const string SecretText = "secret detail of the failure";

    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "{}") => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TrackerContext Context(string site = "cloud-1") => new(site, _ => Task.FromResult("access-token"));

    [Fact]
    public async Task A_call_goes_to_the_api_host_with_the_site_the_bearer_token_and_the_body()
    {
        var handler = new StubHandler(_ => Reply(HttpStatusCode.OK, "{\"ok\":true}"));
        using var stack = new JiraClients(() => handler);

        using var got = await stack.Api.PostAsync(Context(), "/rest/api/3/search/jql?x=1", new { jql = "a" }, CancellationToken.None);
        using var put = await stack.Api.PutAsync(Context(), "/rest/api/3/webhook/refresh", new { webhookIds = new[] { 1 } }, CancellationToken.None);

        Assert.True(got.RootElement.GetProperty("ok").GetBoolean());
        var request = handler.Seen[0];
        Assert.Equal("https://api.atlassian.com/ex/jira/cloud-1/rest/api/3/search/jql?x=1", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(("Bearer", "access-token"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal("{\"jql\":\"a\"}", handler.Bodies[0]);
        Assert.Equal("{\"webhookIds\":[1]}", handler.Bodies[1]);
        Assert.Equal(HttpMethod.Put, handler.Seen[1].Method);
    }

    [Fact]
    public async Task A_site_id_can_never_change_the_path_of_the_call()
    {
        var handler = new StubHandler(_ => Reply(HttpStatusCode.OK));
        using var stack = new JiraClients(() => handler);

        using var _ = await stack.Api.GetAsync(Context("evil/../../x?y=1#z"), "/rest/api/3/myself", CancellationToken.None);

        var uri = handler.Seen.Single().RequestUri!;
        Assert.Equal("api.atlassian.com", uri.Host);
        Assert.StartsWith("/ex/jira/evil%2F..%2F..%2Fx%3Fy%3D1%23z/rest/api/3/myself", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("", uri.Query);
    }

    [Fact]
    public async Task A_429_is_waited_out_and_the_call_is_made_again()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } }
            : Reply(HttpStatusCode.OK, "{\"n\":2}"));
        using var stack = new JiraClients(() => handler);

        using var doc = await stack.Api.GetAsync(Context(), "/rest/api/3/field", CancellationToken.None);

        Assert.Equal(2, doc.RootElement.GetProperty("n").GetInt32());
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task A_site_that_keeps_saying_429_ends_in_rate_limited_after_three_tries()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } });
        using var stack = new JiraClients(() => handler);

        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.Api.GetAsync(Context(), "/rest/api/3/field", CancellationToken.None));

        Assert.Equal(TrackerException.Codes.RateLimited, e.Code);
        Assert.Equal(3, handler.Seen.Count);
    }

    [Fact]
    public async Task A_wait_for_a_retry_can_be_cancelled()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(15)) } });
        using var stack = new JiraClients(() => handler);
        using var cts = new CancellationTokenSource();

        var call = stack.Api.GetAsync(Context(), "/rest/api/3/field", cts.Token);
        await JiraKit.UntilAsync(() => Task.FromResult(handler.Seen.Count == 1), "the first call");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(handler.Seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerException.Codes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TrackerException.Codes.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, TrackerException.Codes.NotFound)]
    [InlineData(HttpStatusCode.Conflict, TrackerException.Codes.Changed)]
    [InlineData(HttpStatusCode.BadRequest, TrackerException.Codes.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerException.Codes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, TrackerException.Codes.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TrackerException.Codes.Unavailable)]
    [InlineData(HttpStatusCode.Gone, TrackerException.Codes.Unavailable)]
    public async Task A_failure_status_becomes_its_code_and_the_response_text_is_never_in_the_exception(HttpStatusCode status, string code)
    {
        using var stack = new JiraClients(() => new StubHandler(_ => Reply(status, $"{{\"message\":\"{SecretText}\"}}")));

        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.Api.GetAsync(Context(), "/rest/api/3/myself", CancellationToken.None));

        Assert.Equal(code, e.Code);
        Assert.Equal(code, e.Message);
        Assert.DoesNotContain(SecretText, e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_content_is_an_empty_object_and_a_broken_body_is_unavailable()
    {
        using var empty = new JiraClients(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var broken = new JiraClients(() => new StubHandler(_ => Reply(HttpStatusCode.OK, "<html>not json</html>")));

        using var doc = await empty.Api.GetAsync(Context(), "/x", CancellationToken.None);

        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        var e = await Assert.ThrowsAsync<TrackerException>(() => broken.Api.GetAsync(Context(), "/x", CancellationToken.None));
        Assert.Equal(TrackerException.Codes.Unavailable, e.Code);
    }

    [Fact]
    public async Task A_network_failure_or_a_timeout_is_unavailable()
    {
        using var down = new JiraClients(() => new ThrowingHandler(new HttpRequestException("no route")));
        using var slow = new JiraClients(() => new ThrowingHandler(new TaskCanceledException("timeout")));

        Assert.Equal(TrackerException.Codes.Unavailable, (await Assert.ThrowsAsync<TrackerException>(() => down.Api.GetAsync(Context(), "/x", CancellationToken.None))).Code);
        Assert.Equal(TrackerException.Codes.Unavailable, (await Assert.ThrowsAsync<TrackerException>(() => slow.Api.GetAsync(Context(), "/x", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_missing_context_is_refused_at_once()
    {
        using var stack = new JiraClients();

        await Assert.ThrowsAsync<ArgumentNullException>(() => stack.Api.GetAsync(null!, "/x", CancellationToken.None));
    }
}
