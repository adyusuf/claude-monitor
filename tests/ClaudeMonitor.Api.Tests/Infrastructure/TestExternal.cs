using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ClaudeMonitor.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>
/// Stands in for the provider round trip when a test only needs "the provider said X": with the test header
/// ("subject|email|verified|name") the external cookie scheme answers from it; without it, the real cookie handler
/// runs, so the tests that drive the whole round trip through <see cref="ProviderStub"/> use the real thing.
/// </summary>
public static class TestExternal
{
    public const string Header = "X-Test-External";
}

public sealed class TestExternalHandler(IOptionsMonitor<CookieAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : CookieAuthenticationHandler(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers[TestExternal.Header].ToString();
        if (value.Length == 0) return base.HandleAuthenticateAsync();
        var parts = value.Split('|');
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, parts[0]) };
        if (parts[1].Length > 0) claims.Add(new Claim(ClaimTypes.Email, parts[1]));
        claims.Add(new Claim(ExternalProviders.EmailVerifiedClaim, parts[2]));
        if (parts.Length > 3) claims.Add(new Claim(ClaimTypes.Name, parts[3]));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Schemes.External));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Schemes.External)));
    }
}

/// <summary>The providers' token and user endpoints, answered locally (the OAuth handler's back channel).</summary>
public static class ProviderStub
{
    public static string GitHubEmail { get; set; } = "";
    public static bool GitHubVerified { get; set; } = true;
    public static string GoogleEmail { get; set; } = "";

    public static readonly HttpMessageHandler Handler = new Stub();

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var body = url switch
            {
                _ when url.Contains("access_token", StringComparison.Ordinal) || url.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal)
                    => """{"access_token":"provider-token","token_type":"bearer","expires_in":3600}""",
                _ when url.EndsWith("/user/emails", StringComparison.Ordinal)
                    => $$"""[{"email":"other@users.noreply.github.com","primary":false,"verified":true},{"email":"{{GitHubEmail}}","primary":true,"verified":{{(GitHubVerified ? "true" : "false")}}}]""",
                _ when url.EndsWith("/user", StringComparison.Ordinal)
                    => """{"id":424242,"login":"octo","name":"Octo Person"}""",
                _ when url.Contains("userinfo", StringComparison.Ordinal)
                    => $$"""{"sub":"g-777","email":"{{GoogleEmail}}","email_verified":true,"name":"Google Person"}""",
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
