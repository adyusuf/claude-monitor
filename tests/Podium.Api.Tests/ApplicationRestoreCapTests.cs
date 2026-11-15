using System.Net;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Restoring an application counts against the project's cap of active ones, like creating one.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApplicationRestoreCapTests(ApiFactory api)
{
    [Fact]
    public async Task An_application_cannot_be_restored_past_the_cap_of_active_ones()
    {
        var owner = await api.NewClient().SignedUpAsync("pa-cap");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Many")).GetProperty("id").GetGuid();
        var ids = new List<Guid>();
        for (var batch = 0; batch < 5; batch++)
        {
            ids.AddRange(await Task.WhenAll(Enumerable.Range(0, 20).Select(async n =>
                (await ProjectKit.CreateApplicationAsync(owner, project, $"App {batch}-{n}")).GetProperty("id").GetGuid())));
        }

        Assert.Equal(ApplicationEndpoints.PerProjectMax, ids.Count);
        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{ids[0]}")).EnsureSuccessStatusCode();
        (await ProjectKit.CreateApplicationAsync(owner, project, "The hundredth")).GetProperty("id").GetGuid();

        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/applications/{ids[0]}/restore")));
        (await owner.SendAsync(HttpMethod.Delete, $"/api/applications/{ids[1]}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/applications/{ids[0]}/restore")).StatusCode);
    }
}
