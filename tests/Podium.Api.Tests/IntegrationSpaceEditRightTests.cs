using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// Q-4 (F-336): the spaces list tells the caller whether they may edit the imported project, so the page offers "import sprints" only to someone the API
/// will let do it (the API check stays: it answers 403 to everyone else). The field is added; the others are unchanged.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationSpaceEditRightTests(ApiFactory api)
{
    private static JsonElement Space(JsonElement list, string key) => list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    [Fact]
    public async Task The_spaces_list_says_canEdit_true_for_the_project_manager_and_the_admin_false_for_a_contributor_a_viewer_and_a_space_that_is_not_imported()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            site.Space("ABC", "Alpha");
            site.Space("XYZ", "Zulu");
        });
        await JiraKit.ImportAsync(w, "ABC");
        var project = await JiraKit.ProjectIdAsync(w, "ABC");
        var contributor = await RemoteKit.MemberAsync(api, w.Admin, "se-contributor");
        var manager = await RemoteKit.MemberAsync(api, w.Admin, "se-manager");
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "se-viewer", "viewer");
        (await w.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{contributor.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        (await w.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{manager.Id}", new { role = "manager" })).EnsureSuccessStatusCode();

        var asAdmin = await w.Admin.GetJsonAsync($"{w.Base}/spaces");
        var asManager = await manager.GetJsonAsync($"{w.Base}/spaces");
        var asContributor = await contributor.GetJsonAsync($"{w.Base}/spaces");
        var asViewer = await viewer.GetJsonAsync($"{w.Base}/spaces");

        Assert.Equal((true, true, false, false), (Space(asAdmin, "ABC").GetProperty("canEdit").GetBoolean(), Space(asManager, "ABC").GetProperty("canEdit").GetBoolean(),
            Space(asContributor, "ABC").GetProperty("canEdit").GetBoolean(), Space(asViewer, "ABC").GetProperty("canEdit").GetBoolean()));
        Assert.False(Space(asAdmin, "XYZ").GetProperty("canEdit").GetBoolean());
        Assert.Equal(("Alpha", true), (Space(asContributor, "ABC").GetProperty("name").GetString(), Space(asContributor, "ABC").GetProperty("imported").GetBoolean()));
    }

    [Fact]
    public async Task The_field_agrees_with_the_import_call_a_person_with_canEdit_false_gets_403_and_one_with_true_may_import()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var spaces = await s.W.Member.GetJsonAsync($"{s.W.World.Base}/spaces");
        Assert.False(Space(spaces, JiraWriteKit.Key).GetProperty("canEdit").GetBoolean());

        var refused = await s.W.Member.PostAsync($"{s.W.World.Base}/spaces/{JiraWriteKit.Key}/sprints/import", new { sprintIds = new[] { s.PlanB.IdText } });
        var allowed = await s.W.World.Admin.PostAsync($"{s.W.World.Base}/spaces/{JiraWriteKit.Key}/sprints/import", new { sprintIds = new[] { s.PlanB.IdText } });

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(Space(await s.W.World.Admin.GetJsonAsync($"{s.W.World.Base}/spaces"), JiraWriteKit.Key).GetProperty("canEdit").GetBoolean());
    }
}
