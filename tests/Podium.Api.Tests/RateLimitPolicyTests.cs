using System.Net;
using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Podium.Api.Tests;

/// <summary>
/// Every endpoint that carries the shared "auth" rate-limit policy, one row each (grep RequireRateLimiting in src/Podium.Api).
/// A dropped <c>RequireRateLimiting</c> turns its row red. The limiter runs before authentication, so a client with no session reaches it.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RateLimitPolicyTests(ApiFactory api)
{
    private const int Budget = 3; // LimitedFactory allows 3 auth requests per minute

    [Theory]
    [InlineData("POST", "/api/auth/register")]
    [InlineData("POST", "/api/auth/verify-email")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/auth/password/forgot")]
    [InlineData("POST", "/api/auth/password/reset")]
    [InlineData("POST", "/api/auth/mfa")]
    [InlineData("GET", "/api/auth/external/github")]
    [InlineData("POST", "/api/device/code")]
    [InlineData("GET", "/api/device/lookup/BCDF-GHJK")]
    [InlineData("POST", "/api/device/approve")]
    [InlineData("POST", "/api/device/deny")]
    [InlineData("POST", "/api/agent/token/refresh")]
    [InlineData("POST", "/api/me/delete")]
    [InlineData("POST", "/api/me/mfa/disable")]
    [InlineData("POST", "/api/workspaces/0190a1b2-0000-7000-8000-000000000001/invitations")]
    public async Task An_endpoint_with_the_auth_policy_answers_429_once_the_budget_is_spent(string method, string url)
    {
        Assert.NotNull(api); // the shared fixture has set the database and the other variables this API reads
        await using var limited = new LimitedFactory();
        var http = limited.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i <= Budget; i++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method == "POST") request.Content = JsonContent.Create(new { });
            statuses.Add((await http.SendAsync(request)).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(Budget));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[Budget]);
    }
}
