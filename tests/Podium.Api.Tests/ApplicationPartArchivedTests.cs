using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>An archived application is read-only for its components and releases too.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApplicationPartArchivedTests(ApiFactory api)
{
    private static async Task<Guid> IdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task An_archived_application_takes_no_component_or_release_change_but_keeps_what_it_has()
    {
        var owner = await api.NewClient().SignedUpAsync("pa-owner");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Shop")).GetProperty("id").GetGuid();
        var app = (await ProjectKit.CreateApplicationAsync(owner, project, "Web")).GetProperty("id").GetGuid();
        var component = await IdAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Cart" }));
        var release = await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.0.0" }));
        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{app}")).EnsureSuccessStatusCode();
        var archived = ("status", "archived");

        Assert.Equal(archived, await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/components/{component}", new { name = "Basket" })));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Delete, $"/api/components/{component}")));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/releases/{release}", new { name = "1.0.1" })));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Delete, $"/api/releases/{release}")));

        Assert.Equal("Cart", Assert.Single((await owner.GetJsonAsync($"/api/applications/{app}/components")).EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("1.0.0", Assert.Single((await owner.GetJsonAsync($"/api/applications/{app}/releases")).EnumerateArray()).GetProperty("name").GetString());
        (await owner.PostAsync($"/api/applications/{app}/restore")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, $"/api/components/{component}", new { name = "Basket" })).StatusCode);
    }
}
