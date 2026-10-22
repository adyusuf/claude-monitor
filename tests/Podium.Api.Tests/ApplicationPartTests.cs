using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Components, modules and the release calendar of an application (ADR-0011).</summary>
[Collection(ApiGroup.Name)]
public sealed class ApplicationPartTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Reader, Guid App)> SetUpAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("pt-owner");
        var reader = await RemoteKit.MemberAsync(api, owner, "pt-viewer", "viewer");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Shop")).GetProperty("id").GetGuid();
        return (owner, reader, (await ProjectKit.CreateApplicationAsync(owner, project, "Web")).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static IEnumerable<string?> Names(JsonElement list) => list.EnumerateArray().Select(x => x.GetProperty("name").GetString());

    [Fact]
    public async Task Components_and_modules_are_added_renamed_listed_by_name_and_archived()
    {
        var (owner, reader, app) = await SetUpAsync();

        var cart = await IdAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Cart", kind = "module" }));
        await IdAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Checkout" }));
        await IdAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Árama", kind = "component" }));

        var listed = await reader.GetJsonAsync($"/api/applications/{app}/components");
        Assert.Equal(["Árama", "Cart", "Checkout"], Names(listed));
        Assert.Equal(["component", "module", "component"], listed.EnumerateArray().Select(x => x.GetProperty("kind").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, $"/api/components/{cart}", new { name = "Basket", kind = "module" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/components/{cart}")).StatusCode);
        Assert.Equal(["Árama", "Checkout"], Names(await owner.GetJsonAsync($"/api/applications/{app}/components")));
        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/components/{cart}", new { name = "Basket" })));
    }

    [Fact]
    public async Task A_component_name_is_unique_without_case_or_accent_and_must_be_valid()
    {
        var (owner, _, app) = await SetUpAsync();
        await IdAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Şablon" }));

        Assert.Equal(("name", "name_taken"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "SABLON" })));
        Assert.Equal(("name", "invalid_name"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = " " })));
        Assert.Equal(("kind", "invalid_value"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "X", kind = "service" })));
    }

    [Fact]
    public async Task A_reader_and_a_stranger_cannot_change_components_or_releases()
    {
        var (owner, reader, app) = await SetUpAsync();
        var stranger = await api.NewClient().SignedUpAsync("pt-stranger");
        var component = await IdAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Core" }));
        var release = await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.0.0", plannedDate = "2026-12-01" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/applications/{app}/components", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/components/{component}", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Delete, $"/api/components/{component}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/applications/{app}/releases", new { name = "2.0.0" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/releases/{release}", new { name = "9.9.9" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Delete, $"/api/releases/{release}")).StatusCode);

        foreach (var (method, url) in new[] { (HttpMethod.Get, $"/api/applications/{app}/components"), (HttpMethod.Get, $"/api/applications/{app}/releases"),
                     (HttpMethod.Put, $"/api/components/{component}"), (HttpMethod.Delete, $"/api/components/{component}"),
                     (HttpMethod.Put, $"/api/releases/{release}"), (HttpMethod.Delete, $"/api/releases/{release}") })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(method, url, method == HttpMethod.Put ? new { name = "Mine" } : null)).StatusCode);
        }
    }

    [Fact]
    public async Task The_release_calendar_is_ordered_by_date_with_undated_releases_last_and_a_cancelled_one_leaves_it()
    {
        var (owner, _, app) = await SetUpAsync();
        await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "Backlog release" }));
        var later = await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.1.0", plannedDate = "2027-02-01" }));
        await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.0.0", plannedDate = "2026-12-01" }));

        var listed = await owner.GetJsonAsync($"/api/applications/{app}/releases");
        Assert.Equal(["1.0.0", "1.1.0", "Backlog release"], Names(listed));
        Assert.Equal("2026-12-01", listed[0].GetProperty("plannedDate").GetString());
        Assert.Equal("planned", listed[0].GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/releases/{later}")).StatusCode);
        Assert.Equal(["1.0.0", "Backlog release"], Names(await owner.GetJsonAsync($"/api/applications/{app}/releases")));
        Assert.Equal(("status", "cancelled"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/releases/{later}", new { name = "1.1.0" })));
        await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.1.0", plannedDate = "2027-03-01" })); // a cancelled name is free again
    }

    [Fact]
    public async Task A_release_that_went_out_stays_in_the_history_and_a_duplicate_or_odd_release_is_refused()
    {
        var (owner, _, app) = await SetUpAsync();
        var out1 = await IdAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.0.0", status = "released", plannedDate = "2026-11-01" }));

        Assert.Equal(("status", "released"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Delete, $"/api/releases/{out1}")));
        Assert.Equal(("name", "name_taken"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.0.0" })));
        Assert.Equal(("status", "invalid_value"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "2.0.0", status = "shipped" })));
        Assert.Equal(("name", "invalid_name"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "" })));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, $"/api/releases/{out1}", new { name = "1.0.0", plannedDate = "2026-11-02", status = "released" })).StatusCode);
    }

    [Fact]
    public async Task An_archived_application_takes_no_new_components_or_releases()
    {
        var (owner, _, app) = await SetUpAsync();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{app}")).EnsureSuccessStatusCode();

        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Late" })));
        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "Late" })));
        Assert.Empty((await owner.GetJsonAsync($"/api/applications/{app}/components")).EnumerateArray());
    }
}
