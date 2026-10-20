using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using static ClaudeMonitor.Api.Tests.Infrastructure.JiraWriteSprintKit;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Making, changing, starting and deleting a mirrored sprint: Jira first, with the acting person's own account (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorSprintWriteTests(ApiFactory api)
{
    private const string Sprints = "/rest/agile/1.0/sprint";

    private static Task<HttpResponseMessage> Make(SprintSet s, TestUser who, object body) => who.PostAsync($"/api/projects/{s.W.Project}/sprints", body);

    [Fact]
    public async Task A_new_sprint_is_made_on_the_spaces_board_and_then_saved_as_a_mirrored_one()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);

        var response = await Make(s, s.W.Member, new { name = "Next", goal = "Ship it" });

        var view = (await IssueKit.Created(response)).GetProperty("sprint");
        var body = s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Sprints);
        Assert.Equal(("Next", "Ship it", s.W.Space.BoardId), (body["name"]!.GetValue<string>(), body["goal"]!.GetValue<string>(), body["originBoardId"]!.GetValue<long?>()));
        Assert.Equal([s.W.MemberAccount!.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Post, Sprints));
        var made = s.W.Site.Sprints.Values.Single(x => x.Name == "Next");
        Assert.Equal(("future", "Ship it"), (made.State, made.Goal));
        Assert.Equal(s.W.World.ConnectionId, view.GetProperty("sourceConnectionId").GetGuid());
        var link = await s.W.LinkAsync(view.GetProperty("id").GetGuid(), LinkKinds.Sprint);
        Assert.Equal(made.IdText, link.ExternalId);
        Assert.Equal(["POST " + Sprints], s.W.Jira.Writes(s.W.Site));
    }

    [Fact]
    public async Task A_new_sprint_sends_its_dates_as_iso_date_times()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);

        await IssueKit.Created(await Make(s, s.W.Member, new { name = "Dated", startDate = "2026-12-01", endDate = "2026-12-14" }));

        var body = s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Sprints);
        Assert.Equal(("2026-12-01T00:00:00.000Z", "2026-12-14T00:00:00.000Z"), (body["startDate"]!.GetValue<string>(), body["endDate"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_sprint_without_dates_sends_none_and_a_space_without_a_board_cannot_get_a_sprint()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        s.W.Site.Space("NOB", board: false);
        await JiraKit.ImportAsync(s.W.World, "NOB");
        var noBoard = await JiraKit.ProjectIdAsync(s.W.World, "NOB");

        await IssueKit.Created(await Make(s, s.W.Member, new { name = "Open ended" }));
        var refused = await s.W.World.Admin.PostAsync($"/api/projects/{noBoard}/sprints", new { name = "Nowhere" });

        var body = s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Sprints);
        Assert.Equal(["name", "originBoardId"], body.Select(f => f.Key).Order(StringComparer.Ordinal));
        await JiraWriteKit.AssertRefusedAsync(refused, HttpStatusCode.Conflict, "jira_no_board");
        Assert.Empty(await JiraKit.SprintsAsync(s.W.World, noBoard));
        Assert.Single(s.W.Jira.Writes(s.W.Site));
    }

    [Fact]
    public async Task Without_an_account_or_when_jira_refuses_or_rejects_no_sprint_is_made_here()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var lonely = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-lonely");
        (await s.W.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.W.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        var count = (await JiraKit.SprintsAsync(s.W.World, s.W.Project)).Count;

        await JiraWriteKit.AssertRefusedAsync(await Make(s, lonely, new { name = "No account" }), HttpStatusCode.Conflict, "jira_account_required");
        Assert.Empty(s.W.Jira.Writes(s.W.Site));
        await JiraWriteKit.AssertRefusedAsync(await Make(s, s.W.Member, new { goal = "A sprint needs a name in Jira" }), HttpStatusCode.BadRequest, "tracker_rejected");
        s.W.Site.Refuse(HttpMethod.Post, Sprints, HttpStatusCode.Forbidden);
        await JiraWriteKit.AssertRefusedAsync(await Make(s, s.W.Member, new { name = "Forbidden" }), HttpStatusCode.Forbidden, "tracker_forbidden");

        Assert.Equal(count, (await JiraKit.SprintsAsync(s.W.World, s.W.Project)).Count);
        Assert.Equal(3, s.W.Site.Sprints.Count);
    }

    [Fact]
    public async Task Only_the_name_or_goal_that_changed_is_sent_and_a_cleared_goal_is_an_empty_one()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");

        (await EditAsync(s.W.Member, id, new { name = "Plan A renamed" })).EnsureSuccessStatusCode();
        (await EditAsync(s.W.Member, id, new { goal = "" })).EnsureSuccessStatusCode();

        var bodies = s.W.Jira.Bodies(s.W.Site, HttpMethod.Post, Route(s.PlanA));
        Assert.Equal(["name"], bodies[0].Select(f => f.Key));
        Assert.Equal(["goal"], bodies[1].Select(f => f.Key));
        Assert.Equal(("", 2), (bodies[1]["goal"]!.GetValue<string>(), bodies.Count));
        Assert.Equal(("Plan A renamed", ""), (s.PlanA.Name, s.PlanA.Goal));
        var row = await s.RowAsync("Plan A renamed");
        Assert.Equal((new DateOnly(2026, 11, 13), null), (row.EndDate, row.Goal));
    }

    [Fact]
    public async Task Only_the_date_that_changed_is_sent_as_an_iso_date_time()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);

        (await EditAsync(s.W.Member, await s.IdAsync("Plan A"), new { endDate = "2026-11-20" })).EnsureSuccessStatusCode();

        var body = s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Route(s.PlanA));
        Assert.Equal(["endDate"], body.Select(f => f.Key));
        Assert.Equal("2026-11-20T00:00:00.000Z", body["endDate"]!.GetValue<string>());
        Assert.Equal(new DateOnly(2026, 11, 20), (await s.RowAsync("Plan A")).EndDate);
    }

    [Fact]
    public async Task A_change_only_podium_keeps_is_saved_without_a_call_and_an_unchanged_form_sends_nothing()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");
        var calls = s.W.Jira.Calls(s.W.Site).Count;

        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(s.W.Member, id, new { capacity = 40 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(s.W.Member, id)).StatusCode);

        Assert.Equal(calls, s.W.Jira.Calls(s.W.Site).Count);
        Assert.Equal(40m, (await s.RowAsync("Plan A")).Capacity);
    }

    [Fact]
    public async Task A_refused_or_account_less_edit_leaves_the_sprint_as_it_was()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");
        var lonely = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-lonely");
        (await s.W.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.W.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        s.W.Site.Refuse(HttpMethod.Post, Route(s.PlanA), HttpStatusCode.Forbidden);

        await JiraWriteKit.AssertRefusedAsync(await EditAsync(lonely, id, new { name = "No account" }), HttpStatusCode.Conflict, "jira_account_required");
        await JiraWriteKit.AssertRefusedAsync(await EditAsync(s.W.Member, id, new { name = "Refused" }), HttpStatusCode.Forbidden, "tracker_forbidden");

        Assert.Equal(("Plan A", "Plan A"), ((await s.RowAsync("Plan A")).Name, s.PlanA.Name));
    }

    [Fact]
    public async Task Starting_a_sprint_sends_the_active_state_with_its_dates_next_to_a_sprint_that_is_already_active()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");

        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(s.W.Member, id)).StatusCode);

        var body = s.W.Jira.Sent(s.W.Site, HttpMethod.Post, Route(s.PlanA));
        Assert.Equal(("active", "2026-11-02T00:00:00.000Z", "2026-11-13T00:00:00.000Z"), (body["state"]!.GetValue<string>(), body["startDate"]!.GetValue<string>(), body["endDate"]!.GetValue<string>()));
        Assert.Equal([s.W.MemberAccount!.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Post, Route(s.PlanA)));
        Assert.Equal(("active", "active", "active"), (s.PlanA.State, s.Now.State, await SprintKit.StatusAsync(s.W.Member, id)));
        Assert.NotNull((await s.RowAsync("Plan A")).StartedAt);
    }

    [Fact]
    public async Task A_sprint_without_dates_does_not_start_and_calls_nothing_while_native_sprints_still_allow_one_active()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var calls = s.W.Jira.Calls(s.W.Site).Count;

        var response = await SprintKit.StartAsync(s.W.Member, await s.IdAsync("Plan B"));

        Assert.Equal(("startDate", "dates_required"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(calls, s.W.Jira.Calls(s.W.Site).Count);
        var admin = s.W.World.Admin;
        var native = (await ProjectKit.CreateProjectAsync(admin, "Native sprints")).GetProperty("id").GetGuid();
        var one = await SprintKit.SprintAsync(admin, native);
        var two = await SprintKit.SprintAsync(admin, native);
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(admin, one)).StatusCode);
        Assert.Equal(("status", "sprint_active"), await ProjectKit.ProblemAsync(await SprintKit.StartAsync(admin, two)));
    }

    [Fact]
    public async Task A_start_without_an_account_leaves_the_sprint_planned()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api, linkMember: false);
        var id = await s.IdAsync("Plan A");

        await JiraWriteKit.AssertRefusedAsync(await SprintKit.StartAsync(s.W.Member, id), HttpStatusCode.Conflict, "jira_account_required");

        Assert.Equal(("planned", "future", 0), (await SprintKit.StatusAsync(s.W.Member, id), s.PlanA.State, s.W.Jira.Writes(s.W.Site).Count));
    }

    [Fact]
    public async Task A_start_that_jira_refuses_leaves_the_sprint_planned()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");
        s.W.Site.Refuse(HttpMethod.Post, Route(s.PlanA), HttpStatusCode.Forbidden);

        await JiraWriteKit.AssertRefusedAsync(await SprintKit.StartAsync(s.W.Member, id), HttpStatusCode.Forbidden, "tracker_forbidden");

        Assert.Equal(("planned", "future"), (await SprintKit.StatusAsync(s.W.Member, id), s.PlanA.State));
    }

    [Fact]
    public async Task Deleting_a_planned_sprint_deletes_it_in_jira_and_archives_it_here_and_its_issues_go_back_to_the_backlog()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");
        Assert.NotNull(await s.SprintOfAsync("WRT-5"));

        var response = await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["DELETE " + Route(s.PlanA)], s.W.Jira.Writes(s.W.Site));
        Assert.True(s.PlanA.Deleted);
        Assert.Null(s.W.Site.Find("WRT-5")!.SprintId);
        Assert.Null(await s.SprintOfAsync("WRT-5"));
        await using var db = api.Db();
        Assert.NotNull((await db.Sprints.SingleAsync(x => x.Id == id)).ArchivedAt);
    }

    [Fact]
    public async Task Deleting_is_refused_without_an_account_and_when_jira_refuses_and_never_for_an_active_sprint()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var plan = await s.IdAsync("Plan A");
        var now = await s.IdAsync("Now");
        var lonely = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-lonely");
        (await s.W.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.W.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        s.W.Site.Refuse(HttpMethod.Delete, Route(s.PlanA), HttpStatusCode.Forbidden);

        await JiraWriteKit.AssertRefusedAsync(await lonely.SendAsync(HttpMethod.Delete, $"/api/sprints/{plan}"), HttpStatusCode.Conflict, "jira_account_required");
        await JiraWriteKit.AssertRefusedAsync(await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{plan}"), HttpStatusCode.Forbidden, "tracker_forbidden");
        var active = await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{now}");

        Assert.Equal(("status", "not_planned"), await ProjectKit.ProblemAsync(active));
        Assert.Equal((false, 1), (s.PlanA.Deleted, s.W.Jira.Writes(s.W.Site).Count));
        Assert.Equal(2, (await s.W.Member.GetJsonAsync($"/api/projects/{s.W.Project}/sprints")).GetProperty("items").GetArrayLength() - 1);
    }
}
