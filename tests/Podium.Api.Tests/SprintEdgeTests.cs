using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The edges of a sprint's life: where its issues land when it goes away, and what a change may not undo (ADR-0011 §5).</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintEdgeTests(ApiFactory api)
{
    [Fact]
    public async Task Deleting_a_planned_sprint_returns_in_flight_issues_to_ready()
    {
        var t = await IssueKit.TeamAsync(api);
        var planned = await SprintKit.SprintAsync(t.Owner, t.Project);
        var progress = await IssueKit.IssueAsync(t.Owner, t.Project, "In progress", "story", new { status = "in_progress" });
        var review = await IssueKit.IssueAsync(t.Owner, t.Project, "In review", "story", new { status = "in_review" });
        var done = await IssueKit.IssueAsync(t.Owner, t.Project, "Finished", "story", new { status = "done" });
        (await SprintKit.AddAsync(t.Owner, planned, progress, review, done)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{planned}")).StatusCode);

        Assert.Equal(("ready", "ready", "done"), (await SprintKit.IssueStatusAsync(t.Owner, progress), await SprintKit.IssueStatusAsync(t.Owner, review), await SprintKit.IssueStatusAsync(t.Owner, done)));
        Assert.Equal((null, null, null), (await SprintKit.IssueSprintAsync(t.Owner, progress), await SprintKit.IssueSprintAsync(t.Owner, review), await SprintKit.IssueSprintAsync(t.Owner, done)));
        Assert.Equal(["In progress", "In review"], await IssueKit.TitlesAsync(t.Owner, $"/api/projects/{t.Project}/issues?scope=backlog"));
    }

    [Fact]
    public async Task An_active_sprint_keeps_its_dates_while_a_planned_one_may_drop_them()
    {
        var t = await IssueKit.TeamAsync(api);
        var active = await SprintKit.SprintAsync(t.Owner, t.Project);
        var planned = await SprintKit.SprintAsync(t.Owner, t.Project);
        (await SprintKit.StartAsync(t.Owner, active)).EnsureSuccessStatusCode();

        Assert.Equal(("startDate", "active_dates_required"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{active}", new { goal = "x", endDate = "2026-11-13" })));
        Assert.Equal(("endDate", "active_dates_required"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{active}", new { goal = "x", startDate = "2026-11-02" })));
        var s = (await SprintKit.ViewAsync(t.Owner, active)).GetProperty("sprint");
        Assert.Equal(("2026-11-02", "2026-11-13", "Ship it"), (s.GetProperty("startDate").GetString(), s.GetProperty("endDate").GetString(), s.GetProperty("goal").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{active}", new { startDate = "2026-11-02", endDate = "2026-11-20" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{planned}", new { goal = "No dates yet" })).StatusCode);
    }

    [Fact]
    public async Task A_team_member_who_became_a_viewer_does_not_block_later_edits_but_cannot_join_anew()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { goal = "Team", teamUserIds = new[] { t.Owner.Id, t.Reviewer.Id } });
        var other = await SprintKit.SprintAsync(t.Owner, t.Project);
        (await t.Owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{t.Owner.WorkspaceId}/members/{t.Reviewer.Id}", new { role = "viewer" })).EnsureSuccessStatusCode();
        Task<HttpResponseMessage> Put(Guid id, object body) => t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{id}", body);

        Assert.Equal(HttpStatusCode.NoContent, (await Put(sprint, new { goal = "Changed", teamUserIds = new[] { t.Owner.Id, t.Reviewer.Id } })).StatusCode);
        Assert.Equal(2, (await SprintKit.ViewAsync(t.Owner, sprint)).GetProperty("team").GetArrayLength());
        Assert.Equal(("teamUserIds", "not_a_member"), await ProjectKit.ProblemAsync(await Put(other, new { teamUserIds = new[] { t.Reviewer.Id } })));
        Assert.Equal(HttpStatusCode.NoContent, (await Put(sprint, new { goal = "Dropped", teamUserIds = new[] { t.Owner.Id } })).StatusCode);
        Assert.Equal(("teamUserIds", "not_a_member"), await ProjectKit.ProblemAsync(await Put(sprint, new { teamUserIds = new[] { t.Owner.Id, t.Reviewer.Id } })));
        Assert.Equal(1, (await SprintKit.ViewAsync(t.Owner, sprint)).GetProperty("team").GetArrayLength());
    }

    [Fact]
    public async Task Restoring_an_unfinished_issue_of_a_closed_sprint_brings_it_back_to_the_backlog_and_a_finished_one_stays()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var flight = await IssueKit.IssueAsync(t.Owner, t.Project, "In flight", "story", new { status = "in_progress" });
        var done = await IssueKit.IssueAsync(t.Owner, t.Project, "Finished", "story", new { status = "done" });
        (await SprintKit.AddAsync(t.Owner, sprint, flight, done)).EnsureSuccessStatusCode();
        (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        foreach (var id in new[] { flight, done }) (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{id}")).EnsureSuccessStatusCode();
        (await SprintKit.CloseAsync(t.Owner, sprint)).EnsureSuccessStatusCode();

        foreach (var id in new[] { flight, done }) Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostAsync($"/api/issues/{id}/restore")).StatusCode);

        Assert.Equal((null, "ready"), (await SprintKit.IssueSprintAsync(t.Owner, flight), await SprintKit.IssueStatusAsync(t.Owner, flight)));
        Assert.Equal((sprint, "done"), (await SprintKit.IssueSprintAsync(t.Owner, done), await SprintKit.IssueStatusAsync(t.Owner, done)));
        Assert.Equal(["In flight"], await IssueKit.TitlesAsync(t.Owner, $"/api/projects/{t.Project}/issues?scope=backlog"));
    }

    [Fact]
    public async Task Restoring_an_issue_of_an_open_sprint_keeps_it_there()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var flight = await IssueKit.IssueAsync(t.Owner, t.Project, "In flight", "story", new { status = "in_progress" });
        (await SprintKit.AddAsync(t.Owner, sprint, flight)).EnsureSuccessStatusCode();
        (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{flight}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostAsync($"/api/issues/{flight}/restore")).StatusCode);

        Assert.Equal((sprint, "in_progress"), (await SprintKit.IssueSprintAsync(t.Owner, flight), await SprintKit.IssueStatusAsync(t.Owner, flight)));
    }
}
