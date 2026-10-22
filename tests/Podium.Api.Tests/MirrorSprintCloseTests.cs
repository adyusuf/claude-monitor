using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Podium.Api.Tests.Infrastructure.JiraWriteSprintKit;

namespace Podium.Api.Tests;

/// <summary>Closing a mirrored sprint and moving issues in and out of one: the move is written to Jira first, the local rows follow (ADR-0015 §7, §8a).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorSprintCloseTests(ApiFactory api)
{
    private static string[] Keys(System.Text.Json.Nodes.JsonObject body) => [.. body["issues"]!.AsArray().Select(k => k!.GetValue<string>()).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Closing_moves_the_unfinished_issues_to_the_chosen_mirrored_sprint_in_jira_first_then_closes_the_sprint()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var target = await s.IdAsync("Plan A");

        var response = await SprintKit.CloseAsync(s.W.Member, await s.IdAsync("Now"), target);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["POST " + Route(s.PlanA, "/issue"), "POST " + Route(s.Now)], s.W.Jira.Writes(s.W.Site)); // the move, then the close
        Assert.Equal(["WRT-6", "WRT-7"], Keys(s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Route(s.PlanA, "/issue"))));
        Assert.Equal("closed", s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Route(s.Now))["state"]!.GetValue<string>());
        Assert.Equal([s.W.MemberAccount!.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Post, Route(s.PlanA, "/issue")));
        Assert.Equal([s.W.MemberAccount.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Post, Route(s.Now)));
        Assert.Equal((s.PlanA.Id, s.PlanA.Id, s.Now.Id, "closed"), (s.W.Site.Find("WRT-6")!.SprintId, s.W.Site.Find("WRT-7")!.SprintId, s.W.Site.Find("WRT-8")!.SprintId, s.Now.State));
        Assert.Equal((target, target, "closed"), (await s.SprintOfAsync("WRT-6"), await s.SprintOfAsync("WRT-7"), await SprintKit.StatusAsync(s.W.Member, await s.IdAsync("Now"))));
        Assert.Equal(await s.IdAsync("Now"), await s.SprintOfAsync("WRT-8")); // finished work stays in its sprint
    }

    [Fact]
    public async Task Closing_without_a_target_moves_the_unfinished_issues_to_the_jira_backlog_and_keeps_their_status_which_jira_keeps_too()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var before = (await s.W.RowAsync("WRT-7")).Status;

        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.CloseAsync(s.W.Member, await s.IdAsync("Now"))).StatusCode);

        Assert.Equal(["POST /rest/agile/1.0/backlog/issue", "POST " + Route(s.Now)], s.W.Jira.Writes(s.W.Site));
        Assert.Equal(["WRT-6", "WRT-7"], Keys(s.W.Jira.Sent(s.W.Site, HttpMethod.Post, "/rest/agile/1.0/backlog/issue")));
        Assert.Equal((null, null), (s.W.Site.Find("WRT-6")!.SprintId, s.W.Site.Find("WRT-7")!.SprintId));
        Assert.Equal((null, null), (await s.SprintOfAsync("WRT-6"), await s.SprintOfAsync("WRT-7")));
        Assert.Equal(before, (await s.W.RowAsync("WRT-7")).Status); // the status is Jira's, and Jira keeps it
    }

    [Fact]
    public async Task Moving_issues_into_a_sprint_keeps_the_stored_stamps_in_step_so_the_next_edit_is_no_conflict()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.W.IdAsync("WRT-2");

        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.AddAsync(s.W.Member, await s.IdAsync("Plan B"), id)).StatusCode);

        Assert.Equal(FakeJira.Stamp(s.W.Site.Find("WRT-2")!.Updated), (await s.W.LinkAsync(id)).ExternalUpdated);
        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(s.W.Member, id, new { title = "Edited right after the move" })).StatusCode);
    }

    [Fact]
    public async Task A_sprint_with_nothing_unfinished_is_closed_without_a_move()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        foreach (var key in new[] { "WRT-6", "WRT-7" }) s.W.ChangeInJira(key, i => { i.Category = "done"; i.Status = "Done"; });
        await JiraKit.ImportSprintsAsync(s.W.World, JiraWriteKit.Key, s.Now.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.CloseAsync(s.W.Member, await s.IdAsync("Now"))).StatusCode);

        Assert.Equal(["POST " + Route(s.Now)], s.W.Jira.Writes(s.W.Site));
        Assert.Equal("done", (await s.W.RowAsync("WRT-7")).Status);
    }

    [Fact]
    public async Task A_target_sprint_that_is_not_from_the_same_connection_is_out_of_scope_and_nothing_is_written()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var local = await s.LocalSprintAsync("Podium only");

        var response = await SprintKit.CloseAsync(s.W.Member, await s.IdAsync("Now"), local);

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "mirrored_scope");
        Assert.Empty(s.W.Jira.Writes(s.W.Site));
        Assert.Equal("active", await SprintKit.StatusAsync(s.W.Member, await s.IdAsync("Now")));
    }

    [Fact]
    public async Task A_close_without_an_account_or_with_a_failed_move_or_close_leaves_the_sprint_and_its_issues_here_as_they_were()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var now = await s.IdAsync("Now");
        var lonely = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-lonely");
        (await s.W.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.W.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();

        await JiraWriteKit.AssertRefusedAsync(await SprintKit.CloseAsync(lonely, now), HttpStatusCode.Conflict, "jira_account_required");
        s.W.Site.Refuse(HttpMethod.Post, "/rest/agile/1.0/backlog/issue", HttpStatusCode.Forbidden);
        await JiraWriteKit.AssertRefusedAsync(await SprintKit.CloseAsync(s.W.Member, now), HttpStatusCode.Forbidden, "tracker_forbidden");
        s.W.Site.Refuse(HttpMethod.Post, Route(s.Now), HttpStatusCode.BadRequest);
        await JiraWriteKit.AssertRefusedAsync(await SprintKit.CloseAsync(s.W.Member, now), HttpStatusCode.BadRequest, "tracker_rejected");

        Assert.Equal("active", await SprintKit.StatusAsync(s.W.Member, now));
        Assert.Equal((now, now, "in_progress"), (await s.SprintOfAsync("WRT-6"), await s.SprintOfAsync("WRT-7"), (await s.W.RowAsync("WRT-7")).Status));
        Assert.Equal("active", s.Now.State);
    }

    [Fact]
    public async Task Issues_are_added_to_a_mirrored_sprint_in_jira_first_and_then_here()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var plan = await s.IdAsync("Plan B");

        var response = await SprintKit.AddAsync(s.W.Member, plan, await s.W.IdAsync("WRT-2"), await s.W.IdAsync("WRT-3"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["POST " + Route(s.PlanB, "/issue")], s.W.Jira.Writes(s.W.Site));
        Assert.Equal(["WRT-2", "WRT-3"], Keys(s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Route(s.PlanB, "/issue"))));
        Assert.Equal([s.W.MemberAccount!.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Post, Route(s.PlanB, "/issue")));
        Assert.Equal((s.PlanB.Id, s.PlanB.Id), (s.W.Site.Find("WRT-2")!.SprintId, s.W.Site.Find("WRT-3")!.SprintId));
        Assert.Equal((plan, plan), (await s.SprintOfAsync("WRT-2"), await s.SprintOfAsync("WRT-3")));
    }

    [Fact]
    public async Task Fifty_issues_go_in_one_call_and_a_fifty_first_is_refused_before_jira_is_called()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api, extraIssues: 51);
        var extra = (await JiraKit.IssuesAsync(s.W.World, s.W.Project)).Where(i => i.Title.StartsWith("Extra", StringComparison.Ordinal)).Select(i => i.Id).ToList();
        var plan = await s.IdAsync("Plan B");
        Assert.Equal(51, extra.Count);

        var tooMany = await SprintKit.AddAsync(s.W.Member, plan, [.. extra]);
        var fifty = await SprintKit.AddAsync(s.W.Member, plan, [.. extra.Take(50)]);

        Assert.Equal(("issueIds", "limit_reached"), await ProjectKit.ProblemAsync(tooMany));
        Assert.Equal(HttpStatusCode.NoContent, fifty.StatusCode);
        Assert.Equal(50, Assert.Single(s.W.Jira.Bodies(s.W.Site, HttpMethod.Post, Route(s.PlanB, "/issue")))["issues"]!.AsArray().Count);
        Assert.Equal(50, s.W.Site.Spaces.SelectMany(x => x.Issues).Count(i => i.SprintId == s.PlanB.Id));
    }

    [Fact]
    public async Task A_mirrored_issue_in_a_podium_only_sprint_and_a_podium_only_issue_in_a_mirrored_one_are_out_of_scope()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var local = await s.LocalSprintAsync("Podium only");
        var localIssue = await s.LocalIssueAsync("Podium only issue");

        var intoLocal = await SprintKit.AddAsync(s.W.Member, local, await s.W.IdAsync("WRT-2"));
        var intoMirrored = await SprintKit.AddAsync(s.W.Member, await s.IdAsync("Plan B"), localIssue);
        var mixed = await SprintKit.AddAsync(s.W.Member, await s.IdAsync("Plan B"), localIssue, await s.W.IdAsync("WRT-2"));

        await JiraWriteKit.AssertRefusedAsync(intoLocal, HttpStatusCode.Conflict, "mirrored_scope");
        await JiraWriteKit.AssertRefusedAsync(intoMirrored, HttpStatusCode.Conflict, "mirrored_scope");
        await JiraWriteKit.AssertRefusedAsync(mixed, HttpStatusCode.Conflict, "mirrored_scope");
        Assert.Empty(s.W.Jira.Writes(s.W.Site));
        Assert.Null(await s.SprintOfAsync("WRT-2"));
        await using var db = api.Db();
        Assert.Null((await db.Issues.AsNoTracking().SingleAsync(i => i.Id == localIssue)).SprintId);
    }

    [Fact]
    public async Task A_podium_only_sprint_takes_podium_only_issues_without_a_call_to_jira()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var local = await s.LocalSprintAsync("Podium only");
        var localIssue = await s.LocalIssueAsync("Podium only issue");

        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.AddAsync(s.W.Member, local, localIssue)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{local}/issues/{localIssue}")).StatusCode);

        Assert.Empty(s.W.Jira.Writes(s.W.Site));
    }

    [Fact]
    public async Task Taking_an_issue_out_of_a_sprint_puts_it_in_the_jira_backlog_and_a_refusal_or_missing_account_keeps_it_in()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var now = await s.IdAsync("Now");
        var issue = await s.W.IdAsync("WRT-6");
        var lonely = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-lonely");
        (await s.W.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.W.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        s.W.Site.Refuse(HttpMethod.Post, "/rest/agile/1.0/backlog/issue", HttpStatusCode.Forbidden);

        await JiraWriteKit.AssertRefusedAsync(await lonely.SendAsync(HttpMethod.Delete, $"/api/sprints/{now}/issues/{issue}"), HttpStatusCode.Conflict, "jira_account_required");
        await JiraWriteKit.AssertRefusedAsync(await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{now}/issues/{issue}"), HttpStatusCode.Forbidden, "tracker_forbidden");
        Assert.Equal(now, await s.SprintOfAsync("WRT-6"));
        var response = await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{now}/issues/{issue}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["WRT-6"], Keys(s.W.Jira.Bodies(s.W.Site, HttpMethod.Post, "/rest/agile/1.0/backlog/issue")[1]));
        Assert.Null(s.W.Site.Find("WRT-6")!.SprintId);
        Assert.Null(await s.SprintOfAsync("WRT-6"));
    }
}
