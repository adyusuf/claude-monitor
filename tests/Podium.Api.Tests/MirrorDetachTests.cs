using System.Net;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// The mirror lock applies only while the connection is not disconnected, and an imported project can be detached for good (F-106, F-262):
/// after a disconnect the project and its issues are Podium's to edit, and "detach" clears the source link.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorDetachTests(ApiFactory api)
{
    private static readonly string[] Fields =
    [
        "name", "purpose", "ownerUserId", "sponsor", "stakeholders", "budgetAmount", "budgetCurrency", "startDate", "endDate", "itemTypes", "priorityScale",
        "estimationUnit", "sprintLengthDays", "definitionOfReady", "definitionOfDone",
    ];

    private static async Task<HttpResponseMessage> EditProjectAsync(TestUser who, Guid project, object changes)
    {
        var view = await who.GetJsonAsync($"/api/projects/{project}");
        return await who.SendAsync(HttpMethod.Put, $"/api/projects/{project}", IssueKit.Merge(Fields.ToDictionary(f => f, f => (object?)view.GetProperty(f)), changes));
    }

    private static async Task DisconnectAsync(WriteWorld w) =>
        (await w.World.Admin.SendAsync(HttpMethod.Delete, $"/api/integrations/{w.World.ConnectionId}")).EnsureSuccessStatusCode();

    private static Task<HttpResponseMessage> DetachAsync(TestUser who, Guid project) => who.PostAsync($"/api/projects/{project}/detach");

    [Fact]
    public async Task While_the_connection_is_active_the_project_says_it_is_mirrored_and_keeps_its_lock()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        var view = await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}");

        Assert.True(view.GetProperty("mirrorActive").GetBoolean());
        await JiraWriteKit.AssertRefusedAsync(await EditProjectAsync(w.World.Admin, w.Project, new { name = "Renamed here" }), HttpStatusCode.Conflict, "mirrored_field_locked");
    }

    [Fact]
    public async Task A_connection_that_needs_a_new_sign_in_still_locks_the_name_because_it_comes_back()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == w.World.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Reauthorize));
        }

        Assert.True((await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}")).GetProperty("mirrorActive").GetBoolean());
        await JiraWriteKit.AssertRefusedAsync(await EditProjectAsync(w.World.Admin, w.Project, new { name = "Renamed here" }), HttpStatusCode.Conflict, "mirrored_field_locked");
    }

    [Fact]
    public async Task After_a_disconnect_the_name_and_purpose_are_podiums_to_edit_and_the_source_link_is_still_shown()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await DisconnectAsync(w);

        var response = await EditProjectAsync(w.World.Admin, w.Project, new { name = "Ours now", purpose = "Our own purpose" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}");
        Assert.Equal(("Ours now", "Our own purpose", false), (view.GetProperty("name").GetString(), view.GetProperty("purpose").GetString(), view.GetProperty("mirrorActive").GetBoolean()));
        Assert.Equal(w.World.ConnectionId, view.GetProperty("sourceConnectionId").GetGuid());
    }

    [Fact]
    public async Task After_a_disconnect_an_issue_is_made_edited_and_archived_here_without_a_call_to_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var story = await w.IdAsync("WRT-2");
        await DisconnectAsync(w);
        var calls = w.Jira.Calls(w.Site).Count;

        var created = await w.Member.PostAsync($"/api/projects/{w.Project}/issues", new { title = "Made after the disconnect", type = "story" });
        var edited = await JiraWriteKit.EditAsync(w.Member, story, new { title = "Edited here", type = "task", status = "in_progress", priority = "3" });
        var moved = await w.Member.SendAsync(HttpMethod.Put, $"/api/issues/{story}/position", new { });
        var archived = await w.Member.SendAsync(HttpMethod.Delete, $"/api/issues/{story}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent], new[] { edited.StatusCode, moved.StatusCode, archived.StatusCode });
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Edited here", "task", "in_progress"), (row.Title, row.Type, row.Status));
        Assert.NotNull(row.ArchivedAt);
    }

    [Fact]
    public async Task The_owner_detaches_a_disconnected_project_which_clears_its_source_and_its_link_and_is_audited()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await DisconnectAsync(w);

        var response = await DetachAsync(w.World.Admin, w.Project);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("sourceConnectionId").ValueKind);
        Assert.False(view.GetProperty("mirrorActive").GetBoolean());
        await using var db = api.Db();
        Assert.False(await db.IntegrationLinks.AnyAsync(l => l.Kind == LinkKinds.Project && l.LocalId == w.Project));
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(a => a.TargetType == "project" && a.TargetId == w.Project.ToString() && a.Action == AuditActions.ProjectDetached).ToListAsync());
    }

    [Fact]
    public async Task A_project_that_is_still_connected_cannot_be_detached()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        await JiraWriteKit.AssertRefusedAsync(await DetachAsync(w.World.Admin, w.Project), HttpStatusCode.Conflict, "mirrored_connected");
        Assert.Equal(w.World.ConnectionId, (await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}")).GetProperty("sourceConnectionId").GetGuid());
    }

    [Fact]
    public async Task Only_the_owner_or_an_admin_detaches_and_a_project_made_in_podium_has_nothing_to_detach()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await DisconnectAsync(w);
        var own = (await ProjectKit.CreateProjectAsync(w.World.Admin, "Made here")).GetProperty("id").GetGuid();
        var stranger = await api.NewClient().SignedUpAsync("dt-stranger");

        Assert.Equal(HttpStatusCode.Forbidden, (await DetachAsync(w.Member, w.Project)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DetachAsync(stranger, w.Project)).StatusCode);
        await JiraWriteKit.AssertRefusedAsync(await DetachAsync(w.World.Admin, own), HttpStatusCode.Conflict, "not_mirrored");
        Assert.Equal(w.World.ConnectionId, (await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}")).GetProperty("sourceConnectionId").GetGuid());
    }

    [Fact]
    public async Task Detaching_twice_answers_not_mirrored_the_second_time()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await DisconnectAsync(w);
        (await DetachAsync(w.World.Admin, w.Project)).EnsureSuccessStatusCode();

        await JiraWriteKit.AssertRefusedAsync(await DetachAsync(w.World.Admin, w.Project), HttpStatusCode.Conflict, "not_mirrored");
    }

    [Fact]
    public async Task A_detached_project_edits_its_issues_and_its_name_here()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await DisconnectAsync(w);
        (await DetachAsync(w.World.Admin, w.Project)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-3"), new { title = "Free" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EditProjectAsync(w.World.Admin, w.Project, new { name = "Free project" })).StatusCode);
    }
}
