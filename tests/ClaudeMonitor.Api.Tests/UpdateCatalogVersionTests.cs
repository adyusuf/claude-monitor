using System.Net.Http.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// The catalog applies the strict x.y.z the agent also requires (UpdateVersion): an entry the agent would call malformed is
/// ignored here, so a lax one (say "1.2" or "1.2.3-beta") can neither be offered nor hide a valid, lower one.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class UpdateCatalogVersionTests : IDisposable
{
    private readonly ApiFactory api;
    private readonly ManifestFile manifest;

    public UpdateCatalogVersionTests(ApiFactory api)
    {
        this.api = api;
        manifest = new ManifestFile(api);
    }

    public void Dispose() => manifest.Dispose();

    private async Task<UpdateOffer?> OfferedAsync(params Dictionary<string, string?>[] entries)
    {
        var user = await api.NewClient().SignedUpAsync("upd-ver");
        var agent = await user.ConnectAgentAsync();
        manifest.Publish(entries);
        var response = await agent.Http.GetAsync("/api/agent/latest?os=macos&arch=arm64");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<UpdateOffer>(TestUser.Json) : null;
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3-beta")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    public async Task An_entry_whose_version_is_not_a_strict_x_y_z_is_ignored_and_a_valid_lower_one_is_offered_instead(string version)
    {
        var offer = await OfferedAsync(ManifestFile.Entry(version, file: "lax.zip"), ManifestFile.Entry("0.3.1", file: "good.zip"));

        Assert.NotNull(offer);
        Assert.Equal("0.3.1", offer.Version);
        Assert.EndsWith("/downloads/good.zip", offer.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-beta")]
    [InlineData(" 0.2.0")]
    public async Task An_entry_whose_minimum_supported_is_not_a_strict_x_y_z_is_ignored_and_a_valid_lower_one_is_offered_instead(string minimum)
    {
        var offer = await OfferedAsync(ManifestFile.Entry("9.9.9", file: "lax.zip", minSupported: minimum), ManifestFile.Entry("0.3.1", file: "good.zip"));

        Assert.NotNull(offer);
        Assert.Equal("0.3.1", offer.Version);
        Assert.EndsWith("/downloads/good.zip", offer.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manifest_with_only_lax_versions_offers_nothing()
    {
        Assert.Null(await OfferedAsync(ManifestFile.Entry("1.2"), ManifestFile.Entry("2.0.0", minSupported: "1.2")));
    }

    [Fact]
    public async Task A_strict_entry_that_is_higher_still_wins_over_the_lower_ones()
    {
        var offer = await OfferedAsync(ManifestFile.Entry("1.2", file: "lax.zip"), ManifestFile.Entry("0.3.1", file: "low.zip"),
            ManifestFile.Entry("1.2.3", file: "high.zip", minSupported: "1.0.0"));

        Assert.Equal("1.2.3", offer!.Version);
        Assert.EndsWith("/downloads/high.zip", offer.Url, StringComparison.Ordinal);
    }
}
