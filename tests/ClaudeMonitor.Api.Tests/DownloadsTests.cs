using System.Net;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>/downloads/* carries the agent builds and their checksums; the single-page app never answers for it.</summary>
[Collection(ApiGroup.Name)]
public sealed class DownloadsTests : IDisposable
{
    private const string Sums = "0123456789abcdef  cm-agent-macos-arm64.zip\n";
    private readonly HttpClient http;
    private readonly string folder;

    public DownloadsTests(ApiFactory api)
    {
        http = api.NewClient().Http;
        folder = Path.Combine(api.WebRoot, "downloads");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SHA256SUMS"), Sums);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"version\":\"0.3.1\"}");
        File.WriteAllBytes(Path.Combine(folder, "cm-agent.zip"), [0x50, 0x4b, 3, 4]);
        File.WriteAllBytes(Path.Combine(folder, "cm-agent.sig"), [1, 2, 3]);
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public async Task The_checksum_file_without_an_extension_is_served_as_text_not_as_the_page()
    {
        var response = await http.GetAsync("/downloads/SHA256SUMS");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Sums, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoCache);
    }

    [Theory]
    [InlineData("manifest.json", "application/json")]
    [InlineData("cm-agent.zip", "application/x-zip-compressed")]
    [InlineData("cm-agent.sig", "application/octet-stream")]
    public async Task Every_download_is_served_with_its_own_type(string file, string type)
    {
        var response = await http.GetAsync("/downloads/" + file);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("/downloads/no-such-file")]
    [InlineData("/downloads/SHA256SUMS.missing")]
    [InlineData("/downloads/")]
    public async Task A_missing_download_is_a_404_never_the_page(string path)
    {
        var response = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<title>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_paths_still_fall_through_to_the_page()
    {
        Assert.Contains("<title>app</title>", await http.GetStringAsync("/w/1/machines"), StringComparison.Ordinal);
    }
}
