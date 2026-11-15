using Microsoft.AspNetCore.Http;
using Podium.Api;

namespace Podium.Api.Tests;

/// <summary>An https deployment tells browsers to keep to https (F-101); a plain-http one (development) must not, or it would pin localhost.</summary>
public sealed class StrictTransportTests
{
    private static async Task<DefaultHttpContext> RunAsync(bool https)
    {
        var http = new DefaultHttpContext();
        await Startup.SecurityHeaders(http, () => Task.CompletedTask, https);
        return http;
    }

    [Fact]
    public async Task An_https_origin_sends_strict_transport_security_for_a_year_without_subdomains_or_preload()
    {
        var http = await RunAsync(https: true);

        Assert.Equal("max-age=31536000", http.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public async Task A_plain_http_origin_sends_no_strict_transport_security_but_keeps_the_other_headers()
    {
        var http = await RunAsync(https: false);

        Assert.False(http.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
    }
}
