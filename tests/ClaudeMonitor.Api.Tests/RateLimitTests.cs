using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The suite's own API allows a huge number of auth requests; this one is built with a limit of 3 per minute.</summary>
public sealed class LimitedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MONITOR_AUTH_RATE_PER_MINUTE", "3");
    }
}

[Collection(ApiGroup.Name)]
public sealed class RateLimitTests(ApiFactory api)
{
    [Fact]
    public async Task Sign_in_attempts_beyond_the_limit_get_429_and_other_endpoints_do_not()
    {
        Assert.NotNull(api); // the shared fixture has set the database and the other variables this API reads
        await using var limited = new LimitedFactory();
        var http = limited.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await http.PostAsJsonAsync("/api/auth/login", new { email = "nobody@gmail.com", password = "wrong password!" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized,
            HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/version")).StatusCode);
        var device = await http.PostAsJsonAsync("/api/device/code", new { machineKey = "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, device.StatusCode);
    }

    [Fact]
    public async Task The_signed_in_device_endpoints_share_the_auth_budget()
    {
        Assert.NotNull(api); // the shared fixture has set the database and the other variables this API reads
        await using var limited = new LimitedFactory();
        var http = limited.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        // The limiter runs before authentication, so a client with no session can spend the budget and show who carries
        // the policy: an endpoint that answered 401 before the budget is spent answers 429 after it, while an endpoint
        // behind the same session check but without the policy (/api/me) keeps answering 401.
        var code = "BCDF-GHJK";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/device/lookup/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/device/approve", new { userCode = code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/device/deny", new { userCode = code })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.GetAsync("/api/device/lookup/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync("/api/device/approve", new { userCode = code })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync("/api/device/deny", new { userCode = code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me")).StatusCode);
    }
}
