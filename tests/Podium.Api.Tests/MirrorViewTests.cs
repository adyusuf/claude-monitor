using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>What the web needs to show a mirrored row: its Jira key and link, its source, and who it is assigned to (ADR-0015 §4, §8a).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorViewTests(ApiFactory api)
{
    [Fact]
    public async Task An_issue_carries_its_key_url_in_the_list_and_in_the_detail_and_a_podium_only_issue_carries_none()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var admin = w.World.Admin;
        var native = (await ProjectKit.CreateProjectAsync(admin, "Podium only")).GetProperty("id").GetGuid();
        var mine = await IssueKit.IssueAsync(admin, native, "Mine");

        var list = (await w.Member.GetJsonAsync($"/api/projects/{w.Project}/issues")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("title").GetString()!);
        var detail = (await IssueKit.ViewAsync(w.Member, await w.IdAsync("WRT-2"))).GetProperty("issue");

        Assert.Equal(("WRT-2", $"https://{w.Site.Host}/browse/WRT-2"), (list["Story one"].GetProperty("externalKey").GetString(), list["Story one"].GetProperty("externalUrl").GetString()));
        Assert.Equal(("WRT-2", $"https://{w.Site.Host}/browse/WRT-2"), (detail.GetProperty("externalKey").GetString(), detail.GetProperty("externalUrl").GetString()));
        var own = (await IssueKit.ViewAsync(admin, mine)).GetProperty("issue");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (own.GetProperty("externalKey").ValueKind, own.GetProperty("externalUrl").ValueKind));
    }

    [Fact]
    public async Task A_project_and_a_sprint_carry_their_source_connection_in_lists_and_details_and_a_podium_only_one_does_not()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var admin = s.W.World.Admin;
        var native = (await ProjectKit.CreateProjectAsync(admin, "Podium only")).GetProperty("id").GetGuid();
        var nativeSprint = await SprintKit.SprintAsync(admin, native);
        var connection = s.W.World.ConnectionId;

        var projects = (await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/projects")).GetProperty("items").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
        var sprints = (await admin.GetJsonAsync($"/api/projects/{s.W.Project}/sprints")).GetProperty("items").EnumerateArray().ToList();
        var detail = await JiraWriteSprintKit.ViewAsync(admin, await s.IdAsync("Now"));

        Assert.Equal(connection, projects[s.W.Project].GetProperty("sourceConnectionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, projects[native].GetProperty("sourceConnectionId").ValueKind);
        Assert.Equal(connection, (await admin.GetJsonAsync($"/api/projects/{s.W.Project}")).GetProperty("sourceConnectionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync($"/api/projects/{native}")).GetProperty("sourceConnectionId").ValueKind);
        Assert.Equal(3, sprints.Count);
        Assert.All(sprints, x => Assert.Equal(connection, x.GetProperty("sourceConnectionId").GetGuid()));
        Assert.Equal(connection, detail.GetProperty("sourceConnectionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await JiraWriteSprintKit.ViewAsync(admin, nativeSprint)).GetProperty("sourceConnectionId").ValueKind);
    }

    [Fact]
    public async Task The_assignee_is_shown_by_the_jira_persons_name_when_no_member_is_that_person_and_by_the_members_when_one_is()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var outsider = await w.IdAsync("WRT-4");

        var list = (await w.Member.GetJsonAsync($"/api/projects/{w.Project}/issues")).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("title").GetString()!);
        var detail = (await IssueKit.ViewAsync(w.Member, outsider)).GetProperty("issue");

        Assert.Equal(("Jira Outsider", JsonValueKind.Null), (list["Story four"].GetProperty("assigneeName").GetString(), list["Story four"].GetProperty("assigneeUserId").ValueKind));
        Assert.Equal(("Jira Outsider", JsonValueKind.Null), (detail.GetProperty("assigneeName").GetString(), detail.GetProperty("assigneeUserId").ValueKind));
        Assert.Equal(JsonValueKind.Null, list["Story two"].GetProperty("assigneeName").ValueKind);
        (await JiraWriteKit.EditAsync(w.Member, outsider, new { assigneeUserId = w.World.Admin.Id })).EnsureSuccessStatusCode();
        var now = (await IssueKit.ViewAsync(w.Member, outsider)).GetProperty("issue");
        Assert.Equal(("Test Person", w.World.Admin.Id), (now.GetProperty("assigneeName").GetString(), now.GetProperty("assigneeUserId").GetGuid()));
    }
}
