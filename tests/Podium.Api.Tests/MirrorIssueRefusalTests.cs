using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>When Jira cannot or will not take a change, the answer says why and the row stays as it was (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueRefusalTests(ApiFactory api)
{
    private const string Issue = "/rest/api/3/issue/WRT-2";

    [Fact]
    public async Task Without_a_linked_account_the_answer_is_jira_account_required_and_nothing_changes_anywhere()
    {
        var w = await JiraWriteKit.OpenAsync(api, linkMember: false);
        var id = await w.IdAsync("WRT-2");
        var calls = w.Jira.Calls(w.Site).Count;

        var response = await JiraWriteKit.EditAsync(w.Member, id, new { title = "Nope", status = "done" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_account_required");
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Story one", "backlog"), (row.Title, row.Status));
    }

    [Fact]
    public async Task A_change_made_in_jira_since_the_last_sync_is_a_conflict_until_the_sync_has_caught_up()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        w.ChangeInJira("WRT-2", i => i.Summary = "Jira's own title");

        var response = await JiraWriteKit.EditAsync(w.Member, id, new { title = "Mine", status = "done" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_changed");
        Assert.Empty(w.Jira.Writes(w.Site)); // not even the status step
        Assert.Equal(("Story one", "backlog"), ((await w.RowAsync("WRT-2")).Title, (await w.RowAsync("WRT-2")).Status));
        Assert.Equal(("Jira's own title", "To Do"), (w.Site.Find("WRT-2")!.Summary, w.Site.Find("WRT-2")!.Status));

        await JiraKit.ImportAsync(w.World, JiraWriteKit.Key);

        Assert.Equal("Jira's own title", (await w.RowAsync("WRT-2")).Title);
        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(w.Member, id, new { title = "Mine now" })).StatusCode);
        Assert.Equal("Mine now", w.Site.Find("WRT-2")!.Summary);
    }

    [Fact]
    public async Task A_status_the_workflow_has_no_step_for_is_refused_before_anything_else_is_written()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.Site.Workflow.RemoveAll(s => s.Category == "indeterminate");

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { status = "in_progress", title = "Never saved" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_transition_unavailable");
        Assert.Empty(w.Jira.Writes(w.Site));
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Story one", "backlog"), (row.Title, row.Status));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, "tracker_forbidden")]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, "tracker_rejected")]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.BadRequest, "tracker_not_found")]
    [InlineData(HttpStatusCode.Conflict, HttpStatusCode.Conflict, "jira_changed")]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Conflict, "jira_account_required")]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, "tracker_unavailable")]
    public async Task Jiras_own_refusal_of_the_write_is_passed_on_and_the_row_is_left_alone(HttpStatusCode jira, HttpStatusCode answer, string title)
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.Site.Refuse(HttpMethod.Put, Issue, jira);

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Refused", priority = "5" });

        await JiraWriteKit.AssertRefusedAsync(response, answer, title);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Story one", "3"), (row.Title, row.Priority));
        Assert.Equal("Story one", w.Site.Find("WRT-2")!.Summary);
    }

    [Fact]
    public async Task A_refusal_after_the_status_step_still_leaves_the_podium_row_as_it_was()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.Site.Refuse(HttpMethod.Put, Issue, HttpStatusCode.Forbidden);

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Half", status = "in_progress" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Forbidden, "tracker_forbidden");
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Story one", "backlog"), (row.Title, row.Status)); // the next webhook or pass brings the step Jira did take
    }

    [Fact]
    public async Task The_type_of_a_mirrored_issue_cannot_change_even_together_with_other_changes()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var calls = w.Jira.Calls(w.Site).Count;

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { type = "bug", title = "Now a bug" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "mirrored_type_locked");
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("story", "Story one"), (row.Type, row.Title));
    }

    [Fact]
    public async Task A_sign_in_that_atlassian_refuses_is_the_same_as_having_no_account()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await using (var db = api.Db())
        {
            await db.IntegrationAccounts.Where(a => a.UserId == w.Member.Id).ExecuteUpdateAsync(a => a.SetProperty(x => x.Status, AccountStatuses.Reauthorize));
        }

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Nope" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_account_required");
        Assert.Empty(w.Jira.Writes(w.Site));
    }
}
