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
}
