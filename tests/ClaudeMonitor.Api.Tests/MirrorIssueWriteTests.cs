using System.Net;
using System.Text.Json.Nodes;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>A change of a mirrored issue goes to Jira first, with the acting person's own account, and is saved here only when Jira took it (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueWriteTests(ApiFactory api)
{
    private const string Issue = "/rest/api/3/issue/WRT-2";

    private static string[] Names(JsonObject fields) => [.. fields.Select(f => f.Key).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_mirrored_fields_go_to_jira_under_their_own_names_with_the_acting_persons_token_and_then_the_row_changes()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var epic = await w.IdAsync("WRT-1");
        var id = await w.IdAsync("WRT-2");

        var response = await JiraWriteKit.EditAsync(w.Member, id, new
        {
            title = "Story one, renamed",
            description = "new text\nsecond line",
            priority = "1",
            estimate = "8",
            assigneeUserId = w.World.Admin.Id,
            parentId = epic,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var fields = w.Jira.Sent(w.Site, HttpMethod.Put, Issue)["fields"]!.AsObject();
        Assert.Equal(["assignee", "customfield_10016", "description", "parent", "priority", "summary"], Names(fields));
        Assert.Equal(("Story one, renamed", "Highest", w.World.Account.Id, "WRT-1", 8),
            (fields["summary"]!.GetValue<string>(), fields["priority"]!["name"]!.GetValue<string>(), fields["assignee"]!["accountId"]!.GetValue<string>(), fields["parent"]!["key"]!.GetValue<string>(), fields["customfield_10016"]!.GetValue<int>()));
        var paragraphs = fields["description"]!["content"]!.AsArray().Select(p => p!["content"]![0]!["text"]!.GetValue<string>()).ToArray();
        Assert.Equal("doc", fields["description"]!["type"]!.GetValue<string>());
        Assert.Equal(["new text", "second line"], paragraphs);
        Assert.Equal([w.MemberAccount!.Id], w.Jira.Callers(w.Site, HttpMethod.Put, Issue)); // the acting person's account, not the connector's
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Story one, renamed", "new text\nsecond line", "1", "8", w.World.Admin.Id, epic), (row.Title, row.Description, row.Priority, row.Estimate, row.AssigneeUserId, row.ParentId));
        var jira = w.Site.Find("WRT-2")!;
        Assert.Equal(("Story one, renamed", "new text\nsecond line", "Highest", 8d, "WRT-1"), (jira.Summary, jira.Description, jira.Priority, jira.Points, jira.ParentKey));
    }

    [Fact]
    public async Task Only_the_field_that_changed_is_sent()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        (await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { priority = "4" })).EnsureSuccessStatusCode();

        var fields = w.Jira.Sent(w.Site, HttpMethod.Put, Issue)["fields"]!.AsObject();
        Assert.Equal(["priority"], Names(fields));
        Assert.Equal("Low", fields["priority"]!["name"]!.GetValue<string>());
        Assert.Equal(("Story one", "old text", "3"), (w.Site.Find("WRT-2")!.Summary, w.Site.Find("WRT-2")!.Description, (await w.RowAsync("WRT-2")).Estimate));
    }

    [Fact]
    public async Task Clearing_a_field_sends_null_for_it_and_empties_the_row_and_the_issue_in_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        (await JiraWriteKit.EditAsync(w.Member, id, new { assigneeUserId = w.World.Admin.Id, parentId = await w.IdAsync("WRT-1"), estimate = "5" })).EnsureSuccessStatusCode();

        var response = await JiraWriteKit.EditAsync(w.Member, id, new { description = "", priority = "", estimate = "", assigneeUserId = (Guid?)null, parentId = (Guid?)null });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var fields = w.Jira.Bodies(w.Site, HttpMethod.Put, Issue)[1]["fields"]!.AsObject();
        Assert.Equal(["assignee", "customfield_10016", "description", "parent", "priority"], Names(fields));
        Assert.All(fields, f => Assert.Null(f.Value));
        var row = await w.RowAsync("WRT-2");
        Assert.Equal((null, null, null, null, null), (row.Description, row.Priority, row.Estimate, row.AssigneeUserId, row.ParentId));
        var jira = w.Site.Find("WRT-2")!;
        Assert.Equal((null, null, null, null, null), (jira.Description, jira.Priority, jira.Points, jira.Assignee, jira.ParentKey));
    }

    [Theory]
    [InlineData("in_progress", "t21", "In Progress")]
    [InlineData("in_review", "t22", "In Review")]
    [InlineData("done", "t31", "Done")]
    [InlineData("cancelled", "t32", "Won't Do")]
    public async Task A_new_status_is_a_workflow_step_in_jira_and_nothing_else_is_written(string status, string transition, string jiraStatus)
    {
        var w = await JiraWriteKit.OpenAsync(api);

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { status });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["POST /rest/api/3/issue/WRT-2/transitions"], w.Jira.Writes(w.Site));
        Assert.Equal(transition, w.Jira.Sent(w.Site, HttpMethod.Post, Issue + "/transitions")["transition"]!["id"]!.GetValue<string>());
        Assert.Equal(jiraStatus, w.Site.Find("WRT-2")!.Status);
        Assert.Equal(status, (await w.RowAsync("WRT-2")).Status);
    }

    [Fact]
    public async Task A_status_and_a_title_together_move_the_issue_first_then_edit_it_and_read_its_new_stamp()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        (await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { status = "in_progress", title = "Started" })).EnsureSuccessStatusCode();

        Assert.Equal(["GET " + Issue, "GET " + Issue + "/transitions", "POST " + Issue + "/transitions", "PUT " + Issue, "GET " + Issue],
            w.Jira.Calls(w.Site).TakeLast(5));
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Started", "in_progress"), (row.Title, row.Status));
    }

    [Fact]
    public async Task What_only_podium_keeps_is_saved_without_a_call_to_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var app = (await ProjectKit.CreateApplicationAsync(w.World.Admin, w.Project, "Web")).GetProperty("id").GetGuid();
        var id = await w.IdAsync("WRT-2");
        var calls = w.Jira.Calls(w.Site).Count;

        var response = await JiraWriteKit.EditAsync(w.Member, id, new
        {
            technicalDesign = "Use a queue",
            applicationId = app,
            acceptanceCriteria = new[] { new { given = "a story", when = "it is read", then = "it shows" } },
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
        var view = await w.Member.GetJsonAsync($"/api/issues/{id}");
        Assert.Equal(("Use a queue", app, "it shows"), (view.GetProperty("technicalDesign").GetString(), view.GetProperty("issue").GetProperty("applicationId").GetGuid(), view.GetProperty("acceptanceCriteria")[0].GetProperty("then").GetString()));
    }

    [Fact]
    public async Task Sending_the_issue_back_as_it_is_or_with_a_padded_title_calls_jira_not_at_all()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        var calls = w.Jira.Calls(w.Site).Count;

        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(w.Member, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(w.Member, id, new { title = "  Story one  " })).StatusCode);

        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
        Assert.Equal("Story one", (await w.RowAsync("WRT-2")).Title);
    }

    [Fact]
    public async Task A_person_without_a_linked_account_changes_a_podium_only_field_but_not_a_mirrored_one()
    {
        var w = await JiraWriteKit.OpenAsync(api, linkMember: false);
        var id = await w.IdAsync("WRT-2");

        var podium = await JiraWriteKit.EditAsync(w.Member, id, new { technicalDesign = "Note" });
        var mirrored = await JiraWriteKit.EditAsync(w.Member, id, new { title = "Nope" });

        Assert.Equal(HttpStatusCode.NoContent, podium.StatusCode);
        await JiraWriteKit.AssertRefusedAsync(mirrored, HttpStatusCode.Conflict, "jira_account_required");
        Assert.Empty(w.Jira.Writes(w.Site));
    }
}
