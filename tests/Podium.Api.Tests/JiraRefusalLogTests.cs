using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>
/// When Jira refuses a call the log says which call and why (the gateway's own 401 reason, or its WWW-Authenticate challenge), so a missing
/// scope can be told from an expired token. It never carries the access token, the cloud id, the query or a body that is the tool's text.
/// No host, no database: the Atlassian client over a handler that answers what the test says.
/// </summary>
public sealed class JiraRefusalLogTests
{
    private const string Token = "ey-secret-access-token-value";
    private const string CloudId = "cloud-site-id-1234";
    private const string PersonalText = "Alice Example <alice@example.net> may not see ACME-7 salary review";

    private static HttpResponseMessage Answer(HttpStatusCode status, string? body = null, string? challenge = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") };
        if (challenge is not null) response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", challenge));
        return response;
    }

    private static async Task<(ListLogger<JiraApi> Log, TrackerException? Error)> CallAsync(HttpResponseMessage answer, string path = "/rest/agile/1.0/board?projectKeyOrId=POD&maxResults=50")
    {
        var log = new ListLogger<JiraApi>();
        using var clients = new JiraClients(() => new StubHandler(_ => answer), log: log);
        var ctx = new TrackerContext(CloudId, _ => Task.FromResult(Token));
        TrackerException? error = null;
        try
        {
            using var _ = await clients.Api.GetAsync(ctx, path, CancellationToken.None);
        }
        catch (TrackerException e)
        {
            error = e;
        }

        return (log, error);
    }

    private static void AssertNothingSensitive(ListLogger<JiraApi> log)
    {
        foreach (var (_, text) in log.Entries)
        {
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(CloudId, text, StringComparison.Ordinal);
            Assert.DoesNotContain("maxResults", text, StringComparison.Ordinal);
            Assert.DoesNotContain("alice@example.net", text, StringComparison.Ordinal);
            Assert.DoesNotContain("salary", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_401_with_a_challenge_logs_the_call_the_status_and_the_challenge_and_still_fails_the_same_way()
    {
        var (log, error) = await CallAsync(Answer(HttpStatusCode.Unauthorized, PersonalText, "realm=\"api.atlassian.com\", error=\"insufficient_scope\""));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("GET /rest/agile/1.0/board: 401", entry.Text, StringComparison.Ordinal);
        Assert.Contains("insufficient_scope", entry.Text, StringComparison.Ordinal);
        AssertNothingSensitive(log);
        Assert.Equal(TrackerException.Codes.Unauthorized, error?.Code);
    }

    [Fact]
    public async Task A_401_without_a_challenge_logs_the_gateways_own_message()
    {
        var (log, _) = await CallAsync(Answer(HttpStatusCode.Unauthorized, "{\"code\":401,\"message\":\"Unauthorized; scope does not match\"}"));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("401 Unauthorized; scope does not match", entry.Text, StringComparison.Ordinal);
        AssertNothingSensitive(log);
    }

    [Fact]
    public async Task A_401_whose_body_is_not_the_gateways_is_not_read_into_the_log()
    {
        var (log, error) = await CallAsync(Answer(HttpStatusCode.Unauthorized, "{\"message\":\"" + PersonalText + "\",\"errorMessages\":[\"" + PersonalText + "\"]}"));

        var entry = Assert.Single(log.Entries);
        Assert.Contains("401 no reason given", entry.Text, StringComparison.Ordinal);
        AssertNothingSensitive(log);
        Assert.Equal(TrackerException.Codes.Unauthorized, error?.Code);
    }

    [Fact]
    public async Task A_gateway_message_is_one_short_line()
    {
        var long_ = new string('x', 400);
        var (log, _) = await CallAsync(Answer(HttpStatusCode.Unauthorized, "{\"code\":401,\"message\":\"first\\nsecond\\tthird " + long_ + "\"}"));

        var text = Assert.Single(log.Entries).Text;
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\t', text);
        Assert.Contains("first second third", text, StringComparison.Ordinal);
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.True(text.Length < 300, $"the line was {text.Length} characters");
    }

    [Fact]
    public async Task A_403_logs_the_call_and_never_its_body()
    {
        var (log, error) = await CallAsync(Answer(HttpStatusCode.Forbidden, "{\"errorMessages\":[\"" + PersonalText + "\"]}"),
            "/rest/api/3/user/assignable/multiProjectSearch?projectKeys=POD&startAt=0");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("GET /rest/api/3/user/assignable/multiProjectSearch: 403 no reason given", entry.Text, StringComparison.Ordinal);
        AssertNothingSensitive(log);
        Assert.Equal(TrackerException.Codes.Forbidden, error?.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, TrackerException.Codes.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, TrackerException.Codes.Rejected)]
    [InlineData(HttpStatusCode.Conflict, TrackerException.Codes.Changed)]
    public async Task An_ordinary_answer_the_callers_expect_is_not_logged(HttpStatusCode status, string code)
    {
        var (log, error) = await CallAsync(Answer(status, PersonalText));

        Assert.Empty(log.Entries);
        Assert.Equal(code, error?.Code);
    }

    [Fact]
    public async Task A_server_error_logs_only_the_call_and_the_status()
    {
        var (log, error) = await CallAsync(Answer(HttpStatusCode.BadGateway, PersonalText));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("GET /rest/agile/1.0/board: 502", entry.Text, StringComparison.Ordinal);
        AssertNothingSensitive(log);
        Assert.Equal(TrackerException.Codes.Unavailable, error?.Code);
    }

    [Fact]
    public async Task A_good_answer_logs_nothing()
    {
        var (log, error) = await CallAsync(Answer(HttpStatusCode.OK, "{\"values\":[]}"));

        Assert.Empty(log.Entries);
        Assert.Null(error);
    }
}
