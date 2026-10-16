using System.Net;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Api.Update;
using ClaudeMonitor.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>MONITOR_DOWNLOADS_URL: the builds and the signed manifest.json are published on a download host; the API reads the manifest there and offers addresses there.</summary>
public sealed class DownloadsHostTests
{
    private const string Base = "https://downloads.example.net/daemons/prod";

    private static ApiConfig ConfigWith(string? url) => ApiConfig.From(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MONITOR_DOWNLOADS_URL"] = url }).Build(), development: true);

    // ---- the setting ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Base, Base)]
    [InlineData(Base + "/", Base)]
    [InlineData(" " + Base + "/ ", Base)]
    [InlineData("https://downloads.example.net", "https://downloads.example.net")]
    public void The_setting_is_an_https_address_without_a_trailing_slash(string value, string expected) =>
        Assert.Equal(expected, ConfigWith(value).DownloadsUrl);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_the_setting_this_server_serves_the_builds(string? value) => Assert.Null(ConfigWith(value).DownloadsUrl);

    [Theory]
    [InlineData("http://downloads.example.net/daemons")]
    [InlineData("downloads.example.net/daemons")]
    [InlineData("https://downloads.example.net:8443/daemons")]
    [InlineData("https://user:pw@downloads.example.net/daemons")]
    [InlineData("https://downloads.example.net/daemons?x=1")]
    [InlineData("https://downloads.example.net/daemons#x")]
    [InlineData("https://203.0.113.7/daemons")]
    [InlineData("https://localhost/daemons")]
    public void A_setting_that_is_not_a_public_https_address_stops_the_start(string value) =>
        Assert.Throws<InvalidOperationException>(() => ConfigWith(value));

    // ---- the catalog ---------------------------------------------------------------------------------------------

    private sealed class Host(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task The_manifest_is_read_from_the_download_host_and_the_offer_points_there()
    {
        var host = new Host(_ => Json(ManifestFile.Json(ManifestFile.Entry("0.4.0", channel: "prod", file: "cm-agent-macos-arm64.zip"))));
        var catalog = new UpdateCatalog(ConfigWith(Base), host);
        Assert.True(catalog.IsRemote);
        Assert.Null(catalog.Latest("macos", "arm64")); // nothing is known before the first refresh

        await catalog.RefreshAsync(CancellationToken.None);

        var offer = catalog.Latest("macos", "arm64");
        Assert.NotNull(offer);
        Assert.Equal(Base + "/cm-agent-macos-arm64.zip", offer.Url);
        Assert.Equal(("0.4.0", "prod"), (offer.Version, offer.Channel));
        Assert.Equal([Base + "/manifest.json"], host.Requests);
    }

    [Fact]
    public async Task Without_a_download_host_nothing_is_fetched_and_the_local_folder_is_used()
    {
        var catalog = new UpdateCatalog(ConfigWith(null));
        Assert.False(catalog.IsRemote);
        await catalog.RefreshAsync(CancellationToken.None);
        Assert.Null(catalog.Latest("macos", "arm64"));
    }

    [Fact]
    public async Task A_host_that_stops_answering_leaves_the_last_good_manifest_in_place()
    {
        var up = true;
        var host = new Host(_ => up ? Json(ManifestFile.Json(ManifestFile.Entry("0.4.0"))) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var catalog = new UpdateCatalog(ConfigWith(Base), host);
        await catalog.RefreshAsync(CancellationToken.None);
        up = false;

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal("0.4.0", catalog.Latest("macos", "arm64")!.Version);
    }

    [Fact]
    public async Task A_connection_failure_leaves_the_last_good_manifest_in_place()
    {
        var fail = false;
        var host = new Host(_ => fail ? throw new HttpRequestException("down") : Json(ManifestFile.Json(ManifestFile.Entry("0.4.0"))));
        var catalog = new UpdateCatalog(ConfigWith(Base), host);
        await catalog.RefreshAsync(CancellationToken.None);
        fail = true;

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.NotNull(catalog.Latest("macos", "arm64"));
    }

    [Theory]
    [InlineData("<html>not a manifest</html>")]
    [InlineData("{\"format\":\"cm-agent-update/9\",\"entries\":[]}")]
    [InlineData("")]
    public async Task Something_that_is_not_a_manifest_replaces_nothing(string body)
    {
        var good = true;
        var host = new Host(_ => Json(good ? ManifestFile.Json(ManifestFile.Entry("0.4.0")) : body));
        var catalog = new UpdateCatalog(ConfigWith(Base), host);
        await catalog.RefreshAsync(CancellationToken.None);
        good = false;

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal("0.4.0", catalog.Latest("macos", "arm64")!.Version);
    }

    [Fact]
    public async Task A_manifest_larger_than_a_manifest_can_be_is_not_read()
    {
        var host = new Host(_ => Json(new string(' ', 300 * 1024) + ManifestFile.Json(ManifestFile.Entry("0.4.0"))));
        var catalog = new UpdateCatalog(ConfigWith(Base), host);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Null(catalog.Latest("macos", "arm64"));
    }

    [Fact]
    public async Task An_entry_with_a_file_name_that_is_not_plain_is_never_offered_from_the_host()
    {
        var host = new Host(_ => Json(ManifestFile.Json(ManifestFile.Entry("0.4.0", file: "../evil.zip"), ManifestFile.Entry("0.3.9", file: "good.zip"))));
        var catalog = new UpdateCatalog(ConfigWith(Base), host);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(Base + "/good.zip", catalog.Latest("macos", "arm64")!.Url);
    }

    // ---- /downloads/<file> still answers ---------------------------------------------------------------------------

    private static async Task<HttpClient> ServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        DownloadsRedirect.Map(app, Base);
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Theory]
    [InlineData("cm-agent-macos-arm64.zip")]
    [InlineData("SHA256SUMS")]
    [InlineData("manifest.json")]
    public async Task An_old_download_link_is_sent_on_to_the_download_host(string file)
    {
        using var http = await ServerAsync();
        var response = await http.GetAsync("/downloads/" + file);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(new Uri($"{Base}/{file}"), response.Headers.Location);
    }

    [Theory]
    [InlineData("%2e%2e")]
    [InlineData(".hidden")]
    [InlineData("a%20b")]
    public async Task A_name_that_is_not_a_plain_file_is_a_404(string file)
    {
        using var http = await ServerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/downloads/" + file)).StatusCode);
    }
}
