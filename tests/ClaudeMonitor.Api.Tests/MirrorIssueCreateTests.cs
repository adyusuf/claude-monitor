using System.Net;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>A new issue in a mirrored project is made in Jira first and only then here, with its key and link (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueCreateTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> Make(WriteWorld w, TestUser who, object body) => who.PostAsync($"/api/projects/{w.Project}/issues", body);

    [Fact]
    public async Task A_new_issue_is_made_in_jira_with_every_field_then_saved_with_its_key_url_and_link()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var epic = await w.IdAsync("WRT-1");
        var baseline = (await JiraKit.ImportAsync(w.World, JiraWriteKit.Key))[0].GetProperty("updated").GetInt32();

        var response = await Make(w, w.Member, new
        {
            title = "Brand new",
            type = "bug",
            priority = "2",
            estimate = "5",
            description = "line a\nline b",
            assigneeUserId = w.World.Admin.Id,
            parentId = epic,
        });

        var view = (await IssueKit.Created(response)).GetProperty("issue");
        Assert.Equal(["POST /rest/api/3/issue"], w.Jira.Writes(w.Site));
        var fields = w.Jira.Sent(w.Site, HttpMethod.Post, "/rest/api/3/issue")["fields"]!.AsObject();
        Assert.Equal(["assignee", "customfield_10016", "description", "issuetype", "parent", "priority", "project", "summary"], fields.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("WRT", "Bug", "Brand new", "High", w.World.Account.Id, "WRT-1", 5),
            (fields["project"]!["key"]!.GetValue<string>(), fields["issuetype"]!["name"]!.GetValue<string>(), fields["summary"]!.GetValue<string>(), fields["priority"]!["name"]!.GetValue<string>(),
                fields["assignee"]!["accountId"]!.GetValue<string>(), fields["parent"]!["key"]!.GetValue<string>(), fields["customfield_10016"]!.GetValue<int>()));
        Assert.Equal([w.MemberAccount!.Id], w.Jira.Callers(w.Site, HttpMethod.Post, "/rest/api/3/issue"));
        var made = w.Site.Find("WRT-5")!;
        Assert.Equal(("WRT-5", $"https://{w.Site.Host}/browse/WRT-5"), (view.GetProperty("externalKey").GetString(), view.GetProperty("externalUrl").GetString()));
        var row = await w.RowAsync("WRT-5");
        var link = await w.LinkAsync(row.Id);
        Assert.Equal((made.Id, "WRT-5", FakeJira.Stamp(made.Updated)), (link.ExternalId, link.ExternalKey, link.ExternalUpdated));
        Assert.Equal(("Brand new", "bug", "line a\nline b", w.World.Admin.Id), (row.Title, row.Type, row.Description, row.AssigneeUserId));
        Assert.Equal(baseline, (await JiraKit.ImportAsync(w.World, JiraWriteKit.Key))[0].GetProperty("updated").GetInt32()); // the echo changes nothing
        Assert.Equal(5, (await JiraKit.IssuesAsync(w.World, w.Project)).Count);
    }

    [Theory]
    [InlineData("epic", "Epic")]
    [InlineData("story", "Story")]
    [InlineData("bug", "Bug")]
    [InlineData("task", "Task")]
    [InlineData("spike", "Task")]
    public async Task The_type_is_sent_as_the_jira_type_name_and_a_spike_is_a_task_there(string type, string jiraType)
    {
        var w = await JiraWriteKit.OpenAsync(api);

        await IssueKit.Created(await Make(w, w.Member, new { title = "Typed", type }));

        Assert.Equal(jiraType, w.Jira.Sent(w.Site, HttpMethod.Post, "/rest/api/3/issue")["fields"]!["issuetype"]!["name"]!.GetValue<string>());
        Assert.Equal(jiraType, w.Site.Find("WRT-5")!.Type);
    }

    [Theory]
    [InlineData("backlog", false)]
    [InlineData("ready", false)]
    [InlineData("in_progress", true)]
    [InlineData("done", true)]
    public async Task A_status_beyond_ready_is_a_step_after_the_issue_is_made(string status, bool step)
    {
        var w = await JiraWriteKit.OpenAsync(api);

        var view = (await IssueKit.Created(await Make(w, w.Member, new { title = "Moving", status }))).GetProperty("issue");

        Assert.Equal(step ? ["POST /rest/api/3/issue", "POST /rest/api/3/issue/WRT-5/transitions"] : ["POST /rest/api/3/issue"], w.Jira.Writes(w.Site));
        Assert.Equal(status, view.GetProperty("status").GetString());
        Assert.Equal(step ? status != "done" ? "In Progress" : "Done" : "To Do", w.Site.Find("WRT-5")!.Status);
    }

    [Fact]
    public async Task Without_an_account_or_when_jira_rejects_the_issue_no_row_is_made()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");
        (await w.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{w.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();

        await JiraWriteKit.AssertRefusedAsync(await Make(w, lonely, new { title = "No account" }), HttpStatusCode.Conflict, "jira_account_required");
        Assert.Empty(w.Jira.Writes(w.Site));
        w.Site.Refuse(HttpMethod.Post, "/rest/api/3/issue", HttpStatusCode.BadRequest);
        await JiraWriteKit.AssertRefusedAsync(await Make(w, w.Member, new { title = "Rejected" }), HttpStatusCode.BadRequest, "tracker_rejected");

        Assert.Equal(4, (await JiraKit.IssuesAsync(w.World, w.Project)).Count);
        Assert.Equal(4, w.Space.Issues.Count);
    }

    [Fact]
    public async Task A_person_who_is_not_in_jira_cannot_be_the_assignee_of_a_new_issue()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");

        var response = await Make(w, w.Member, new { title = "Who", assigneeUserId = lonely.Id });

        Assert.Equal(("assignee_not_in_jira", HttpStatusCode.BadRequest), (await JiraWriteKit.TitleAsync(response), response.StatusCode));
        Assert.Empty(w.Jira.Writes(w.Site));
        Assert.Equal(4, (await JiraKit.IssuesAsync(w.World, w.Project)).Count);
    }

    [Fact]
    public async Task Jiras_permission_refusal_to_make_the_issue_is_passed_on_and_no_row_is_made()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.Site.Refuse(HttpMethod.Post, "/rest/api/3/issue", HttpStatusCode.Forbidden);

        var response = await Make(w, w.Member, new { title = "Forbidden", estimate = "3" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Forbidden, "tracker_forbidden");
        Assert.Equal(4, (await JiraKit.IssuesAsync(w.World, w.Project)).Count);
    }

    [Fact]
    public async Task A_status_the_workflow_has_no_step_for_never_leaves_a_row_that_disagrees_with_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.Site.Workflow.RemoveAll(s => s.Category == "indeterminate");

        var response = await Make(w, w.Member, new { title = "Stuck", status = "in_progress" });

        if (response.IsSuccessStatusCode)
        {
            Assert.Equal("backlog", (await w.RowAsync("WRT-5")).Status); // saved, so it must say what Jira says
        }
        else
        {
            await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_transition_unavailable");
        }
    }
}
