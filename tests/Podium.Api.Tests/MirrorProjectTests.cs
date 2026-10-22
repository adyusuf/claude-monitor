using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>A mirrored project: its name and purpose are Jira's, the rest of it is Podium's to edit (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorProjectTests(ApiFactory api)
{
    private static readonly string[] Fields =
    [
        "name", "purpose", "ownerUserId", "sponsor", "stakeholders", "budgetAmount", "budgetCurrency", "startDate", "endDate", "itemTypes", "priorityScale",
        "estimationUnit", "sprintLengthDays", "definitionOfReady", "definitionOfDone",
    ];

    /// <summary>The project form as the web sends it back, with some fields changed.</summary>
    private static async Task<HttpResponseMessage> EditAsync(TestUser who, Guid project, object? changes = null)
    {
        var view = await who.GetJsonAsync($"/api/projects/{project}");
        var body = Fields.ToDictionary(f => f, f => (object?)view.GetProperty(f));
        return await who.SendAsync(HttpMethod.Put, $"/api/projects/{project}", IssueKit.Merge(body, changes));
    }

    [Fact]
    public async Task What_jira_does_not_own_is_edited_here_without_a_call_to_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var calls = w.Jira.Calls(w.Site).Count;

        var response = await EditAsync(w.World.Admin, w.Project, new
        {
            sponsor = "Head of delivery",
            budgetAmount = 1200.5m,
            budgetCurrency = "EUR",
            sprintLengthDays = 7,
            definitionOfDone = new[] { "Reviewed", "Deployed" },
            name = "  Write space  ", // the same name, only padded
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
        var view = await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}");
        Assert.Equal(("Write space", "Head of delivery", 7, 2), (view.GetProperty("name").GetString(), view.GetProperty("sponsor").GetString(), view.GetProperty("sprintLengthDays").GetInt32(), view.GetProperty("definitionOfDone").GetArrayLength()));
        Assert.Equal(w.World.ConnectionId, view.GetProperty("sourceConnectionId").GetGuid());
    }

    [Fact]
    public async Task A_new_name_or_purpose_is_refused_because_they_are_jiras()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        await JiraWriteKit.AssertRefusedAsync(await EditAsync(w.World.Admin, w.Project, new { name = "Renamed here" }), HttpStatusCode.Conflict, "mirrored_field_locked");
        await JiraWriteKit.AssertRefusedAsync(await EditAsync(w.World.Admin, w.Project, new { purpose = "Our own purpose" }), HttpStatusCode.Conflict, "mirrored_field_locked");
        await JiraWriteKit.AssertRefusedAsync(await EditAsync(w.World.Admin, w.Project, new { name = "Renamed too", purpose = "And this", sponsor = "Someone" }), HttpStatusCode.Conflict, "mirrored_field_locked");

        var view = await w.World.Admin.GetJsonAsync($"/api/projects/{w.Project}");
        Assert.Equal(("Write space", System.Text.Json.JsonValueKind.Null, System.Text.Json.JsonValueKind.Null), (view.GetProperty("name").GetString(), view.GetProperty("purpose").ValueKind, view.GetProperty("sponsor").ValueKind));
    }

    [Fact]
    public async Task A_purpose_that_jira_sent_can_be_sent_back_unchanged_and_follows_jira_when_it_changes()
    {
        var w = await JiraWriteKit.OpenAsync(api, (_, space) => space.Description = "Jira's words");
        var id = w.Project;

        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(w.World.Admin, id, new { sponsor = "Same purpose" })).StatusCode);
        w.Space.Description = "Jira's new words";
        await JiraKit.ImportAsync(w.World, JiraWriteKit.Key);

        Assert.Equal("Jira's new words", (await w.World.Admin.GetJsonAsync($"/api/projects/{id}")).GetProperty("purpose").GetString());
        await JiraWriteKit.AssertRefusedAsync(await EditAsync(w.World.Admin, id, new { purpose = "Jira's words" }), HttpStatusCode.Conflict, "mirrored_field_locked");
    }

    [Fact]
    public async Task A_mirrored_project_can_still_be_archived_and_restored_here()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var calls = w.Jira.Calls(w.Site).Count;

        Assert.Equal(HttpStatusCode.NoContent, (await w.World.Admin.SendAsync(HttpMethod.Delete, $"/api/projects/{w.Project}")).StatusCode);
        await using (var db = api.Db())
        {
            Assert.Equal(ManagedStatuses.Archived, (await db.Projects.AsNoTracking().SingleAsync(p => p.Id == w.Project)).Status);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await w.World.Admin.PostAsync($"/api/projects/{w.Project}/restore")).StatusCode);

        await using var after = api.Db();
        Assert.Equal(ManagedStatuses.Active, (await after.Projects.AsNoTracking().SingleAsync(p => p.Id == w.Project)).Status);
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
    }
}
