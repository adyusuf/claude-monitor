using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// The agent self-update surface: the manifest the server hands out (GET /api/agent/latest), the update hint in the
/// machine list and the workspace's update mode. manifest.json in the web root's downloads folder is this class's alone.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class UpdateEndpointTests : IDisposable
{
    private readonly ApiFactory api;
    private readonly ManifestFile manifest;

    public UpdateEndpointTests(ApiFactory api)
    {
        this.api = api;
        manifest = new ManifestFile(api);
    }

    public void Dispose() => manifest.Dispose();

    private void Publish(params Dictionary<string, string?>[] entries) => manifest.Publish(entries);

    private void Publish(string content, DateTime? stamp = null) => manifest.Publish(content, stamp);

    private async Task<TestAgent> AgentAsync(string tag = "upd")
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        return await user.ConnectAgentAsync();
    }

    private static Task<HttpResponseMessage> Latest(TestAgent agent, string query = "os=macos&arch=arm64") =>
        agent.Http.GetAsync("/api/agent/latest?" + query);

    private static async Task<string?> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    // ---- GET /api/agent/latest ----

    [Fact]
    public async Task Latest_answers_the_offer_and_builds_the_url_from_the_servers_own_origin()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1", signature: "c2lnbmF0dXJl", minSupported: "0.2.5"));
        var response = await Latest(agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["channel", "minSupported", "sha256", "signature", "url", "version"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("0.3.1", body.GetProperty("version").GetString());
        Assert.Equal(ApiFactory.Origin + "/downloads/" + ManifestFile.MacZip, body.GetProperty("url").GetString());
        Assert.Equal(ManifestFile.Sha, body.GetProperty("sha256").GetString());
        Assert.Equal("c2lnbmF0dXJl", body.GetProperty("signature").GetString());
        Assert.Equal("0.2.5", body.GetProperty("minSupported").GetString());
        Assert.Equal("test", body.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Latest_picks_the_highest_version_by_number_not_by_text()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1", file: "old.zip"), ManifestFile.Entry("0.10.0", file: "new.zip"), ManifestFile.Entry("0.9.9", file: "middle.zip"));
        var offer = await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json);
        Assert.Equal("0.10.0", offer!.Version);
        Assert.EndsWith("/downloads/new.zip", offer.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Latest_does_not_offer_a_build_for_another_os_or_cpu()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1"));
        foreach (var query in new[] { "os=windows&arch=x64", "os=windows&arch=arm64", "os=macos&arch=x64", "os=linux&arch=arm64" })
        {
            var response = await Latest(agent, query);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no_update", await TitleOf(response));
        }
    }

    [Fact]
    public async Task Latest_chooses_among_the_entries_of_the_asked_os_and_cpu_only()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1"), ManifestFile.Entry("9.0.0", os: "windows", arch: "x64", file: "cm-agent-windows-x64.zip"));
        Assert.Equal("0.3.1", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
        Assert.Equal("9.0.0", (await (await Latest(agent, "os=windows&arch=x64")).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task Latest_offers_a_linux_build_to_a_linux_agent_and_nothing_of_another_os(string arch)
    {
        var agent = await AgentAsync();
        var zip = $"cm-agent-linux-{arch}.zip";
        Publish(ManifestFile.Entry("0.3.1"), ManifestFile.Entry("0.4.0", os: OsKinds.Linux, arch: arch, file: zip));
        var offer = await (await Latest(agent, $"os={OsKinds.Linux}&arch={arch}")).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json);
        Assert.Equal("0.4.0", offer!.Version);
        Assert.Equal(ApiFactory.Origin + "/downloads/" + zip, offer.Url);
        Assert.Equal("0.3.1", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version); // macOS keeps its own
    }

    [Fact]
    public async Task Latest_without_a_manifest_is_no_update_not_an_error()
    {
        var agent = await AgentAsync();
        var response = await Latest(agent);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no_update", await TitleOf(response));
    }

    [Theory]
    [InlineData("{\"format\":\"cm-agent-update/2\",\"entries\":[]}")]
    [InlineData("{\"entries\":[]}")]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"cm-agent-update/1\",\"entries\":\"nope\"}")]
    [InlineData("{\"format\":\"cm-agent-update/1\"}")]
    public async Task Latest_with_an_unusable_manifest_is_no_update_not_a_500(string content)
    {
        var agent = await AgentAsync();
        Publish(content);
        var response = await Latest(agent);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no_update", await TitleOf(response));
    }

    [Fact]
    public async Task A_manifest_of_another_format_offers_nothing_even_with_good_looking_entries()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Json(ManifestFile.Entry("0.3.1")).Replace("cm-agent-update/1", "cm-agent-update/2", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await Latest(agent)).StatusCode);
    }

    public static TheoryData<string, string?> UnusableEntries() => new()
    {
        { "file", "../x.zip" },
        { "file", "a/b.zip" },
        { "file", "a\\b.zip" },
        { "file", ".hidden" },
        { "file", "" },
        { "file", null },
        { "file", "x y.zip" },
        { "file", "x?.zip" },
        { "sha256", "abc" },
        { "sha256", ManifestFile.Sha + "0" },
        { "sha256", new string('g', 64) },
        { "sha256", "" },
        { "sha256", null },
        { "version", null },
        { "version", "" },
        { "channel", null },
        { "channel", "" },
        { "os", null },
        { "arch", null },
        { "minSupported", null },
        { "minSupported", "" },
        { "signature", null },
        { "signature", "" },
    };

    [Theory]
    [MemberData(nameof(UnusableEntries))]
    public async Task An_unusable_entry_is_ignored(string field, string? value)
    {
        var agent = await AgentAsync();
        var bad = ManifestFile.Entry("9.9.9");
        bad[field] = value;
        if (field is "os" or "arch") bad.Remove(field);
        var good = ManifestFile.Entry("0.3.1", file: "good.zip");
        Publish(bad, good);
        var offer = await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json);
        Assert.Equal("0.3.1", offer!.Version);
        Assert.EndsWith("/downloads/good.zip", offer.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_with_a_missing_field_is_ignored_and_a_version_that_is_not_a_version_never_wins()
    {
        var agent = await AgentAsync();
        var noSha = ManifestFile.Entry("9.9.9");
        noSha.Remove("sha256");
        Publish(noSha, ManifestFile.Entry("not-a-version"), ManifestFile.Entry("0.3.1", file: "good.zip"));
        Assert.Equal("0.3.1", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
    }

    [Fact]
    public async Task A_manifest_with_only_unusable_entries_offers_nothing()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1", file: "../x.zip"));
        Assert.Equal(HttpStatusCode.NotFound, (await Latest(agent)).StatusCode);
    }

    [Theory]
    [InlineData("os=freebsd&arch=arm64")]
    [InlineData("os=unsupported&arch=x64")]
    [InlineData("os=LINUX&arch=x64")]
    [InlineData("os=macos&arch=mips")]
    [InlineData("os=MACOS&arch=arm64")]
    [InlineData("arch=arm64")]
    [InlineData("os=macos")]
    [InlineData("")]
    public async Task Latest_with_a_bad_query_is_a_400(string query)
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Latest(agent, query)).StatusCode);
    }

    [Fact]
    public async Task Latest_names_the_field_that_is_wrong()
    {
        var agent = await AgentAsync();
        var os = await (await Latest(agent, "os=freebsd&arch=arm64")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(os.GetProperty("errors").TryGetProperty("os", out _));
        var arch = await (await Latest(agent, "os=macos&arch=mips")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(arch.GetProperty("errors").TryGetProperty("arch", out _));
    }

    [Fact]
    public async Task Latest_needs_an_agent_token()
    {
        Publish(ManifestFile.Entry("0.3.1"));
        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agent/latest?os=macos&arch=arm64")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "made-up-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agent/latest?os=macos&arch=arm64")).StatusCode);
    }

    [Fact]
    public async Task Latest_does_not_answer_a_web_session()
    {
        var user = await api.NewClient().SignedUpAsync("upd-web");
        Publish(ManifestFile.Entry("0.3.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/agent/latest?os=macos&arch=arm64")).StatusCode);
    }

    [Fact]
    public async Task An_agent_below_the_minimum_version_can_still_fetch_its_update_while_the_rest_asks_it_to_upgrade()
    {
        var agent = await AgentAsync("upd-old");
        Publish(ManifestFile.Entry("0.3.1"));
        agent.Http.DefaultRequestHeaders.Remove(AgentHeaders.Version);
        agent.Http.DefaultRequestHeaders.Add(AgentHeaders.Version, "0.1.0");
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await agent.Http.GetAsync("/api/agent/settings")).StatusCode);
        var response = await Latest(agent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0.3.1", (await response.Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
    }

    [Fact]
    public async Task A_changed_manifest_is_read_again_even_though_the_catalog_caches()
    {
        var agent = await AgentAsync();
        var stamp = new DateTime(2031, 5, 5, 10, 0, 0, DateTimeKind.Utc);
        Publish(ManifestFile.Json(ManifestFile.Entry("0.3.1")), stamp);
        Assert.Equal("0.3.1", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
        Assert.Equal("0.3.1", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);

        Publish(ManifestFile.Json(ManifestFile.Entry("0.4.0")), stamp.AddMinutes(1));
        Assert.Equal("0.4.0", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);

        // A rewrite of another length is seen even if the clock did not move.
        Publish(ManifestFile.Json(ManifestFile.Entry("0.4.10", file: "longer-name.zip")),
            stamp.AddMinutes(1));
        Assert.Equal("0.4.10", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
    }

    [Fact]
    public async Task A_manifest_that_is_removed_or_broken_after_being_read_stops_offering()
    {
        var agent = await AgentAsync();
        Publish(ManifestFile.Entry("0.3.1"));
        Assert.Equal(HttpStatusCode.OK, (await Latest(agent)).StatusCode);
        Publish("{ half written");
        Assert.Equal(HttpStatusCode.NotFound, (await Latest(agent)).StatusCode);
        Publish(ManifestFile.Entry("0.3.2"));
        Assert.Equal("0.3.2", (await (await Latest(agent)).Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json))!.Version);
        manifest.Delete();
        Assert.Equal(HttpStatusCode.NotFound, (await Latest(agent)).StatusCode);
    }
}
