using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-104: an edit that read the project as active and then waited for the project lock must see an archive that committed meanwhile (the test
/// archives the row while holding it, so the order is forced by the database, not by timing): the edit is refused as archived and changes nothing.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ProjectArchiveRaceTests(ApiFactory api)
{
    [Fact]
    public async Task An_edit_waiting_on_the_project_lock_is_refused_when_an_archive_committed_meanwhile()
    {
        var owner = await api.NewClient().SignedUpAsync("pj-arch-race");
        var id = (await ProjectKit.CreateProjectAsync(owner, "Before archive")).GetProperty("id").GetGuid();
        var body = System.Text.Json.JsonSerializer.SerializeToElement(ProjectKit.Project("Edited while archived"), TestUser.Json);

        var answers = await RowLockHold.RunAsync(api, $"UPDATE projects SET status = 'archived' WHERE id = {id}",
            () => owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", body));

        var refused = Assert.Single(answers);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("archived", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("status")[0].GetString());
        var view = await owner.GetJsonAsync($"/api/projects/{id}");
        Assert.Equal("Before archive", view.GetProperty("name").GetString());
    }
}
