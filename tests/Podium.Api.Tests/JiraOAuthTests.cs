using System.Net;
using System.Text;
using System.Web;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Atlassian's OAuth 2.0 (3LO) flow (ADR-0015 §3): which sites may be called, the sign-in address, and the token calls.</summary>
public sealed class JiraOAuthTests
{
    [Theory]
    [InlineData("acme", "acme.atlassian.net")]
    [InlineData("  ACME  ", "acme.atlassian.net")]
    [InlineData("acme.atlassian.net", "acme.atlassian.net")]
    [InlineData("Acme.Atlassian.NET", "acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net", "acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/ABC/boards/1", "acme.atlassian.net")]
    [InlineData("HTTPS://acme.atlassian.net/jira?x=1", "acme.atlassian.net")]
    [InlineData("a", "a.atlassian.net")]
    [InlineData("my-team-2", "my-team-2.atlassian.net")]
    public void A_sub_domain_in_any_usual_form_gives_the_one_host_that_may_be_called(string input, string expected) =>
        Assert.Equal(expected, JiraOAuth.SiteHost(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil.com")]
    [InlineData("acme.evil.com")]
    [InlineData("acme.atlassian.net.evil.com")]
    [InlineData("evil.com#.atlassian.net")]
    [InlineData("evil.com?.atlassian.net")]
    [InlineData("evil.com/.atlassian.net")]
    [InlineData("evil.com\\.atlassian.net")]
    [InlineData("http://acme.atlassian.net")]
    [InlineData("ftp://acme.atlassian.net")]
    [InlineData("https://evil.com/acme.atlassian.net")]
    [InlineData("https://evil.com@acme.atlassian.net.evil.com")]
    [InlineData("acme:8443")]
    [InlineData("user@acme")]
    [InlineData("-acme")]
    [InlineData("acme_team")]
    [InlineData("ac me")]
    [InlineData("acme.team")] // a dot inside the label would be another host
    [InlineData(".atlassian.net")]
    [InlineData("atlassian.net")]
    [InlineData("acmé")]
    [InlineData("https://")]
    [InlineData("127.0.0.1")]
    public void Anything_else_is_refused(string? input) => Assert.Null(JiraOAuth.SiteHost(input));

    [Theory]
    [InlineData("https://acme.atlassian.net:8443")]
    [InlineData("https://user@acme.atlassian.net")]
    [InlineData("https://user:pw@acme.atlassian.net:99/jira")]
    public void A_port_or_credentials_in_an_address_are_dropped_and_never_reach_the_host(string input) =>
        Assert.Equal("acme.atlassian.net", JiraOAuth.SiteHost(input));

    [Fact]
    public void A_label_of_63_characters_is_the_longest_that_is_accepted()
    {
        Assert.NotNull(JiraOAuth.SiteHost(new string('a', 63)));
        Assert.Null(JiraOAuth.SiteHost(new string('a', 64)));
        Assert.Null(JiraOAuth.SiteHost(new string('a', 64) + ".atlassian.net"));
    }

    [Fact]
    public void The_pkce_challenge_is_the_s256_of_the_verifier_as_in_rfc_7636()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", JiraOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void A_verifier_is_long_url_safe_and_new_every_time()
    {
        var first = JiraOAuth.NewVerifier();
        var second = JiraOAuth.NewVerifier();

        Assert.NotEqual(first, second);
        Assert.InRange(first.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
    }

    [Fact]
    public void The_sign_in_address_carries_the_scopes_the_state_the_challenge_and_the_callback_but_no_secret()
    {
        using var stack = new JiraClients();
        var verifier = JiraOAuth.NewVerifier();

        var url = stack.OAuth.AuthorizeUrl("state-123", verifier);
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.StartsWith($"{stack.Config.AtlassianAuthBase}/authorize?", url, StringComparison.Ordinal);
        Assert.Equal("api.atlassian.com", query["audience"]);
        Assert.Equal(FakeJira.ClientId, query["client_id"]);
        Assert.Equal("state-123", query["state"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(JiraOAuth.Challenge(verifier), query["code_challenge"]);
        Assert.Equal($"{stack.Config.PublicOrigin}/api/integrations/jira/callback", query["redirect_uri"]);
        Assert.Equal(stack.OAuth.RedirectUri, query["redirect_uri"]);
        Assert.Equal(JiraOAuth.Scopes, query["scope"]!.Split(' '));
        Assert.Contains("offline_access", JiraOAuth.Scopes); // without it there is no refresh token
        Assert.Contains("read:jira-work", JiraOAuth.Scopes);
        Assert.DoesNotContain(FakeJira.ClientSecret, url, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier, url, StringComparison.Ordinal); // only its hash leaves
    }

    [Fact]
    public async Task A_code_is_exchanged_for_tokens_with_the_verifier_and_the_apps_credentials()
    {
        using var stack = new JiraClients();
        var site = stack.Jira.Site();
        var account = stack.Jira.Account("Ada", "ada@example.invalid", site);
        var verifier = JiraOAuth.NewVerifier();
        var code = stack.Jira.NewCode(account, JiraOAuth.Challenge(verifier), stack.OAuth.RedirectUri);

        var tokens = await stack.OAuth.ExchangeAsync(code, verifier, CancellationToken.None);

        Assert.StartsWith("at-", tokens.Access, StringComparison.Ordinal);
        Assert.StartsWith("rt-", tokens.Refresh, StringComparison.Ordinal);
        Assert.Equal(3600, tokens.ExpiresInSeconds);
        var request = stack.Jira.Requests.Single(r => r.Path == "/oauth/token");
        Assert.Equal(FakeJira.AuthHost, request.Host);
        Assert.Contains($"\"code_verifier\":\"{verifier}\"", request.Body, StringComparison.Ordinal);
        // The code is single use, and a wrong verifier is refused with a code the person can act on.
        var again = await Assert.ThrowsAsync<TrackerException>(() => stack.OAuth.ExchangeAsync(code, verifier, CancellationToken.None));
        Assert.Equal(TrackerException.Codes.Unauthorized, again.Code);
        var fresh = stack.Jira.NewCode(account, JiraOAuth.Challenge(verifier), stack.OAuth.RedirectUri);
        var wrong = await Assert.ThrowsAsync<TrackerException>(() => stack.OAuth.ExchangeAsync(fresh, JiraOAuth.NewVerifier(), CancellationToken.None));
        Assert.Equal(TrackerException.Codes.Unauthorized, wrong.Code);
    }

    [Fact]
    public async Task A_refresh_gives_a_new_refresh_token_and_the_old_one_is_dead()
    {
        using var stack = new JiraClients();
        var account = stack.Jira.Account("Ada", null);
        var first = (await Exchange(stack, account)).Refresh;

        var second = await stack.OAuth.RefreshAsync(first, CancellationToken.None);

        Assert.NotEqual(first, second.Refresh);
        var reuse = await Assert.ThrowsAsync<TrackerException>(() => stack.OAuth.RefreshAsync(first, CancellationToken.None));
        Assert.Equal(TrackerException.Codes.Unauthorized, reuse.Code); // invalid_grant: the person signs in again
        Assert.Equal(account.CurrentRefreshToken, second.Refresh);
    }

    private static Task<TokenSet> Exchange(JiraClients stack, FakeAccount account)
    {
        var verifier = JiraOAuth.NewVerifier();
        return stack.OAuth.ExchangeAsync(stack.Jira.NewCode(account, JiraOAuth.Challenge(verifier), stack.OAuth.RedirectUri), verifier, CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TrackerException.Codes.Unauthorized)]
    [InlineData(HttpStatusCode.Unauthorized, TrackerException.Codes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TrackerException.Codes.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, TrackerException.Codes.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerException.Codes.Unavailable)]
    public async Task A_refused_token_call_becomes_a_short_code_without_the_response_text(HttpStatusCode status, string code)
    {
        using var stack = new JiraClients(() => new StubHandler(_ => Reply(status, "{\"error_description\":\"secret detail of the failure\"}")));

        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.OAuth.RefreshAsync("rt", CancellationToken.None));

        Assert.Equal(code, e.Code);
        Assert.DoesNotContain("secret detail", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"access_token\":\"a\"}")]
    [InlineData("{\"refresh_token\":\"r\"}")]
    [InlineData("{\"access_token\":\"\",\"refresh_token\":\"r\"}")]
    public async Task A_token_answer_without_both_tokens_is_rejected(string body)
    {
        using var stack = new JiraClients(() => new StubHandler(_ => Reply(HttpStatusCode.OK, body)));

        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.OAuth.RefreshAsync("rt", CancellationToken.None));

        Assert.Equal(TrackerException.Codes.Rejected, e.Code);
    }

    [Theory]
    [InlineData(5, 60)]
    [InlineData(100000, 86400)]
    [InlineData(1800, 1800)]
    public async Task The_lifetime_of_an_access_token_is_kept_within_sane_bounds(int given, int kept)
    {
        using var stack = new JiraClients(() => new StubHandler(_ => Reply(HttpStatusCode.OK, $"{{\"access_token\":\"a\",\"refresh_token\":\"r\",\"expires_in\":{given}}}")));

        Assert.Equal(kept, (await stack.OAuth.RefreshAsync("rt", CancellationToken.None)).ExpiresInSeconds);
    }

    [Fact]
    public async Task A_missing_lifetime_defaults_to_an_hour()
    {
        using var stack = new JiraClients(() => new StubHandler(_ => Reply(HttpStatusCode.OK, "{\"access_token\":\"a\",\"refresh_token\":\"r\"}")));

        Assert.Equal(3600, (await stack.OAuth.RefreshAsync("rt", CancellationToken.None)).ExpiresInSeconds);
    }

    [Fact]
    public async Task The_sites_of_a_login_are_the_https_ones_with_an_id_and_the_account_is_read_from_myself()
    {
        var sites = """
            [{"id":"c-1","url":"https://One.atlassian.net","name":"One"},
             {"id":"c-2","url":"http://two.atlassian.net","name":"Plain http"},
             {"id":"","url":"https://three.atlassian.net"},
             {"url":"https://four.atlassian.net"},
             {"id":"c-5","url":"not a url"},
             {"id":"c-6","url":"https://six.atlassian.net"}]
            """;
        using var stack = new JiraClients(() => new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/myself", StringComparison.Ordinal)
            ? Reply(HttpStatusCode.OK, "{\"accountId\":\"u-1\",\"displayName\":\"Ada\"}") : Reply(HttpStatusCode.OK, sites)));

        var found = await stack.OAuth.SitesAsync("token", CancellationToken.None);
        var me = await stack.OAuth.MeAsync("c-1", "token", CancellationToken.None);

        Assert.Equal([("c-1", "one.atlassian.net", "One"), ("c-6", "six.atlassian.net", "six.atlassian.net")], found.Select(s => (s.Id, s.Host, s.Name)));
        Assert.Equal(("u-1", "Ada"), me);
    }

    [Fact]
    public async Task A_site_list_that_is_not_a_list_is_empty_and_an_account_without_an_id_is_rejected()
    {
        using var empty = new JiraClients(() => new StubHandler(_ => Reply(HttpStatusCode.OK, "{}")));
        using var anonymous = new JiraClients(() => new StubHandler(_ => Reply(HttpStatusCode.OK, "{\"displayName\":\"no id\"}")));

        Assert.Empty(await empty.OAuth.SitesAsync("token", CancellationToken.None));
        var e = await Assert.ThrowsAsync<TrackerException>(() => anonymous.OAuth.MeAsync("c-1", "token", CancellationToken.None));
        Assert.Equal(TrackerException.Codes.Rejected, e.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TrackerException.Codes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TrackerException.Codes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, TrackerException.Codes.Unavailable)]
    public async Task A_refused_site_call_gets_a_code_and_the_bearer_token_is_sent_only_there(HttpStatusCode status, string code)
    {
        var handler = new StubHandler(_ => Reply(status, "{}"));
        using var stack = new JiraClients(() => handler);

        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.OAuth.SitesAsync("the-token", CancellationToken.None));

        Assert.Equal(code, e.Code);
        Assert.Equal(("https://api.atlassian.com/oauth/token/accessible-resources", "the-token"),
            (handler.Seen.Single().RequestUri!.ToString(), handler.Seen.Single().Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task A_network_failure_or_a_timeout_is_unavailable_and_a_cancel_stays_a_cancel()
    {
        using var down = new JiraClients(() => new ThrowingHandler(new HttpRequestException("no route")));
        using var slow = new JiraClients(() => new ThrowingHandler(new TaskCanceledException("timeout")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var cancelled = new JiraClients(() => new ThrowingHandler(new TaskCanceledException("cancelled", null, cts.Token)));

        Assert.Equal(TrackerException.Codes.Unavailable, (await Assert.ThrowsAsync<TrackerException>(() => down.OAuth.RefreshAsync("rt", CancellationToken.None))).Code);
        Assert.Equal(TrackerException.Codes.Unavailable, (await Assert.ThrowsAsync<TrackerException>(() => slow.OAuth.RefreshAsync("rt", CancellationToken.None))).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.OAuth.RefreshAsync("rt", cts.Token));
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A call that fails on the way instead of answering.</summary>
public sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw failure;
}
