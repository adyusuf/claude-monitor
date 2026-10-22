using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The quick status change, archiving and restoring of a mirrored issue, and what a Podium-only issue never does (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueActionTests(ApiFactory api)
{
    private const string Transitions = "/rest/api/3/issue/WRT-2/transitions";

    private static Task<HttpResponseMessage> Quick(TestUser who, Guid issue, string status) => who.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status });

    [Fact]
    public async Task The_quick_status_change_is_a_workflow_step_taken_with_the_acting_persons_account_and_its_stamp_is_kept()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");

        Assert.Equal(HttpStatusCode.NoContent, (await Quick(w.Member, id, "in_review")).StatusCode);

        Assert.Equal("t22", w.Jira.Sent(w.Site, HttpMethod.Post, Transitions)["transition"]!["id"]!.GetValue<string>());
        Assert.Equal([w.MemberAccount!.Id], w.Jira.Callers(w.Site, HttpMethod.Post, Transitions));
        Assert.Equal(("in_review", "In Review"), ((await w.RowAsync("WRT-2")).Status, w.Site.Find("WRT-2")!.Status));
        Assert.Equal(FakeJira.Stamp(w.Site.Find("WRT-2")!.Updated), (await w.LinkAsync(id)).ExternalUpdated);
        // The stamp is in step, so a second step right after is not a conflict.
        Assert.Equal(HttpStatusCode.NoContent, (await Quick(w.Member, id, "done")).StatusCode);
    }

    [Fact]
    public async Task The_quick_status_change_meets_a_change_made_in_jira_since_the_last_sync_as_a_conflict()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        w.ChangeInJira("WRT-2", i => i.Summary = "Jira's own title");

        var response = await Quick(w.Member, id, "in_review");

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_changed");
        Assert.Empty(w.Jira.Writes(w.Site));
        Assert.Equal(("backlog", "To Do"), ((await w.RowAsync("WRT-2")).Status, w.Site.Find("WRT-2")!.Status));
    }

    [Fact]
    public async Task The_quick_status_change_to_the_status_it_already_has_calls_jira_not_at_all()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var calls = w.Jira.Calls(w.Site).Count;

        Assert.Equal(HttpStatusCode.NoContent, (await Quick(w.Member, await w.IdAsync("WRT-2"), "backlog")).StatusCode);

        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
    }

    [Fact]
    public async Task The_quick_status_change_without_an_account_without_a_step_or_against_jiras_refusal_leaves_the_status()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");
        (await w.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{w.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();

        await JiraWriteKit.AssertRefusedAsync(await Quick(lonely, id, "done"), HttpStatusCode.Conflict, "jira_account_required");
        w.Site.Workflow.RemoveAll(s => s.Category == "done");
        await JiraWriteKit.AssertRefusedAsync(await Quick(w.Member, id, "done"), HttpStatusCode.Conflict, "jira_transition_unavailable");
        w.Site.Refuse(HttpMethod.Post, Transitions, HttpStatusCode.Forbidden);
        await JiraWriteKit.AssertRefusedAsync(await Quick(w.Member, id, "in_progress"), HttpStatusCode.Forbidden, "tracker_forbidden");

        Assert.Equal(("backlog", "To Do"), ((await w.RowAsync("WRT-2")).Status, w.Site.Find("WRT-2")!.Status));
        Assert.Equal(["POST " + Transitions], w.Jira.Writes(w.Site)); // only the one that Jira refused
    }

    [Fact]
    public async Task A_mirrored_issue_cannot_be_archived_and_an_archived_one_cannot_be_restored_because_jiras_delete_cannot_be_undone()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        w.Site.Find("WRT-3")!.Deleted = true;
        await JiraKit.PassAsync(w.World);
        var gone = await w.RowAsync("WRT-3");
        Assert.NotNull(gone.ArchivedAt); // archived by the sync, as the ADR says
        var calls = w.Jira.Calls(w.Site).Count;

        await JiraWriteKit.AssertRefusedAsync(await w.Member.SendAsync(HttpMethod.Delete, $"/api/issues/{id}"), HttpStatusCode.Conflict, "mirrored_no_archive");
        await JiraWriteKit.AssertRefusedAsync(await w.Member.PostAsync($"/api/issues/{gone.Id}/restore"), HttpStatusCode.Conflict, "mirrored_no_archive");

        Assert.Null((await w.RowAsync("WRT-2")).ArchivedAt);
        Assert.NotNull((await w.RowAsync("WRT-3")).ArchivedAt);
        Assert.Equal(calls, w.Jira.Calls(w.Site).Count);
    }

    [Fact]
    public async Task A_podium_only_project_next_to_a_connection_makes_no_call_to_jira_for_any_of_its_issues_or_sprints()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var admin = w.World.Admin;
        var project = (await ProjectKit.CreateProjectAsync(admin, "Podium only")).GetProperty("id").GetGuid();
        var calls = w.Jira.Requests.Count;

        var one = await IssueKit.IssueAsync(admin, project, "First");
        var two = await IssueKit.IssueAsync(admin, project, "Second", "bug");
        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(admin, one, new { title = "First, renamed", status = "in_progress", type = "bug" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Quick(admin, two, "done")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Put, $"/api/issues/{one}/position", new { afterIssueId = two })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/issues/{two}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/issues/{two}/restore")).StatusCode);
        var sprint = await SprintKit.SprintAsync(admin, project);
        var mine = await IssueKit.IssueAsync(admin, project, "In a sprint");
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.AddAsync(admin, sprint, mine)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { name = "Renamed", goal = "Other", startDate = "2026-11-03", endDate = "2026-11-14" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(admin, sprint)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.CloseAsync(admin, sprint)).StatusCode);

        Assert.Equal(calls, w.Jira.Requests.Count);
        var view = await IssueKit.ViewAsync(admin, one);
        Assert.Equal(("First, renamed", "bug", System.Text.Json.JsonValueKind.Null), (view.GetProperty("issue").GetProperty("title").GetString(), view.GetProperty("issue").GetProperty("type").GetString(), view.GetProperty("issue").GetProperty("externalKey").ValueKind));
    }
}
