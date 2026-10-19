using System.Net;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The issues of a sprint: adding, removing, closing, the sums and the quick status change (ADR-0011 §5).</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintIssueTests(ApiFactory api)
{
    [Fact]
    public async Task Backlog_issues_go_into_a_sprint_and_leave_the_backlog_view_but_stay_in_the_sprint_filter()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Contributor, t.Project);
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A", "story", new { estimate = "3" });
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B", "story", new { estimate = "5.5", status = "ready" });
        await IssueKit.IssueAsync(t.Owner, t.Project, "C");

        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.AddAsync(t.Contributor, sprint, a, b)).StatusCode);

        Assert.Equal(["C"], await IssueKit.TitlesAsync(t.Owner, $"/api/projects/{t.Project}/issues?scope=backlog"));
        Assert.Equal(["A", "B"], await IssueKit.TitlesAsync(t.Reviewer, $"/api/projects/{t.Project}/issues?sprint={sprint}"));
        Assert.Equal(sprint, await SprintKit.IssueSprintAsync(t.Owner, a));
        var view = await SprintKit.ViewAsync(t.Reviewer, sprint);
        Assert.Equal((2, 8.5m), (view.GetProperty("sprint").GetProperty("issueCount").GetInt32(), view.GetProperty("sprint").GetProperty("committed").GetDecimal()));
        Assert.Equal((1, 1, 0), (view.GetProperty("statusCounts").GetProperty("backlog").GetInt32(), view.GetProperty("statusCounts").GetProperty("ready").GetInt32(), view.GetProperty("statusCounts").GetProperty("done").GetInt32()));
    }

    [Fact]
    public async Task Committed_and_done_are_summed_from_the_estimates_without_cancelled_or_archived_issues()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var ids = new[]
        {
            await IssueKit.IssueAsync(t.Owner, t.Project, "Done", "story", new { estimate = "2", status = "done" }),
            await IssueKit.IssueAsync(t.Owner, t.Project, "Open", "story", new { estimate = "3" }),
            await IssueKit.IssueAsync(t.Owner, t.Project, "Cancelled", "story", new { estimate = "7", status = "cancelled" }),
            await IssueKit.IssueAsync(t.Owner, t.Project, "Archived", "story", new { estimate = "11" }),
            await IssueKit.IssueAsync(t.Owner, t.Project, "No estimate"),
        };
        (await SprintKit.AddAsync(t.Owner, sprint, ids)).EnsureSuccessStatusCode();
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{ids[3]}")).EnsureSuccessStatusCode();

        var s = (await SprintKit.ViewAsync(t.Owner, sprint)).GetProperty("sprint");

        Assert.Equal((4, 5m, 2m), (s.GetProperty("issueCount").GetInt32(), s.GetProperty("committed").GetDecimal(), s.GetProperty("done").GetDecimal()));
    }

    [Fact]
    public async Task Bad_additions_are_refused_and_nothing_is_added()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var taken = await SprintKit.SprintAsync(t.Owner, t.Project);
        var good = await IssueKit.IssueAsync(t.Owner, t.Project, "Good");
        var inOther = await IssueKit.IssueAsync(t.Owner, t.Project, "Taken");
        var gone = await IssueKit.IssueAsync(t.Owner, t.Project, "Gone");
        var foreign = await IssueKit.IssueAsync(other.Owner, other.Project, "Foreign");
        (await SprintKit.AddAsync(t.Owner, taken, inOther)).EnsureSuccessStatusCode();
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{gone}")).EnsureSuccessStatusCode();

        Assert.Equal(("issueIds", "invalid_issue"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, good, inOther)));
        Assert.Equal(("issueIds", "invalid_issue"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, gone)));
        Assert.Equal(("issueIds", "invalid_issue"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, foreign)));
        Assert.Equal(("issueIds", "invalid_issue"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, Guid.NewGuid())));
        Assert.Equal(("issueIds", "invalid_list"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint)));
        Assert.Equal(("issueIds", "limit_reached"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray())));
        Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, good));
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.AddAsync(t.Owner, sprint, good, good)).StatusCode); // the same id twice is one
    }

    [Fact]
    public async Task A_sprint_of_an_application_takes_only_the_issues_of_that_application()
    {
        var t = await IssueKit.TeamAsync(api);
        var second = (await ProjectKit.CreateApplicationAsync(t.Owner, t.Project, "Mobile")).GetProperty("id").GetGuid();
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { applicationId = t.Application });
        var web = await IssueKit.IssueAsync(t.Owner, t.Project, "Web issue", "story", new { applicationId = t.Application });
        var mobile = await IssueKit.IssueAsync(t.Owner, t.Project, "Mobile issue", "story", new { applicationId = second });
        var none = await IssueKit.IssueAsync(t.Owner, t.Project, "No application");

        Assert.Equal(("issueIds", "wrong_scope"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, web, mobile)));
        Assert.Equal(("issueIds", "wrong_scope"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, sprint, none)));
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.AddAsync(t.Owner, sprint, web)).StatusCode);
    }

    [Fact]
    public async Task Removing_an_issue_returns_it_to_the_backlog_and_work_in_flight_becomes_ready()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var flight = await IssueKit.IssueAsync(t.Owner, t.Project, "In flight", "story", new { status = "in_progress" });
        var done = await IssueKit.IssueAsync(t.Owner, t.Project, "Finished", "story", new { status = "done" });
        (await SprintKit.AddAsync(t.Owner, sprint, flight, done)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{flight}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{done}")).StatusCode);

        Assert.Equal(("ready", "done"), (await SprintKit.IssueStatusAsync(t.Owner, flight), await SprintKit.IssueStatusAsync(t.Owner, done)));
        Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, flight));
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{flight}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Reviewer.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{flight}")).StatusCode);
    }

    [Fact]
    public async Task Closing_moves_the_unfinished_issues_to_the_backlog_or_to_a_planned_sprint_and_keeps_the_finished_ones()
    {
        var t = await IssueKit.TeamAsync(api);
        var current = await SprintKit.SprintAsync(t.Owner, t.Project);
        var next = await SprintKit.SprintAsync(t.Owner, t.Project);
        var done = await IssueKit.IssueAsync(t.Owner, t.Project, "Done", "story", new { status = "done" });
        var cancelled = await IssueKit.IssueAsync(t.Owner, t.Project, "Dropped", "story", new { status = "cancelled" });
        var flight = await IssueKit.IssueAsync(t.Owner, t.Project, "Flight", "story", new { status = "in_review" });
        var waiting = await IssueKit.IssueAsync(t.Owner, t.Project, "Waiting");
        (await SprintKit.AddAsync(t.Owner, current, done, cancelled, flight, waiting)).EnsureSuccessStatusCode();
        (await SprintKit.StartAsync(t.Owner, current)).EnsureSuccessStatusCode();

        Assert.Equal(("moveUnfinishedTo", "invalid_sprint"), await ProjectKit.ProblemAsync(await SprintKit.CloseAsync(t.Owner, current, Guid.NewGuid())));
        Assert.Equal(("moveUnfinishedTo", "invalid_sprint"), await ProjectKit.ProblemAsync(await SprintKit.CloseAsync(t.Owner, current, current)));
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.CloseAsync(t.Owner, current, next)).StatusCode);

        Assert.Equal((current, current, next, next), (await SprintKit.IssueSprintAsync(t.Owner, done), await SprintKit.IssueSprintAsync(t.Owner, cancelled), await SprintKit.IssueSprintAsync(t.Owner, flight), await SprintKit.IssueSprintAsync(t.Owner, waiting)));
        Assert.Equal("in_review", await SprintKit.IssueStatusAsync(t.Owner, flight)); // the next sprint keeps the status
        Assert.NotEqual(JsonValueKind.Null, (await SprintKit.ViewAsync(t.Owner, current)).GetProperty("sprint").GetProperty("closedAt").ValueKind);
        Assert.Equal(("status", "not_active"), await ProjectKit.ProblemAsync(await SprintKit.CloseAsync(t.Owner, current)));
        Assert.Equal(("status", "closed"), await ProjectKit.ProblemAsync(await SprintKit.AddAsync(t.Owner, current, await IssueKit.IssueAsync(t.Owner, t.Project, "Late"))));
        Assert.Equal(("status", "closed"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{current}/issues/{done}")));

        (await SprintKit.StartAsync(t.Owner, next)).EnsureSuccessStatusCode();
        (await SprintKit.CloseAsync(t.Owner, next)).EnsureSuccessStatusCode();
        Assert.Equal(("ready", "backlog"), (await SprintKit.IssueStatusAsync(t.Owner, flight), await SprintKit.IssueStatusAsync(t.Owner, waiting)));
        Assert.Equal((null, null, current), (await SprintKit.IssueSprintAsync(t.Owner, flight), await SprintKit.IssueSprintAsync(t.Owner, waiting), await SprintKit.IssueSprintAsync(t.Owner, done)));
    }

    [Fact]
    public async Task A_target_sprint_must_share_the_scope_of_the_closing_one()
    {
        var t = await IssueKit.TeamAsync(api);
        var webSprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { applicationId = t.Application, startDate = "2026-11-02", endDate = "2026-11-13" });
        var wide = await SprintKit.SprintAsync(t.Owner, t.Project);
        (await SprintKit.StartAsync(t.Owner, webSprint)).EnsureSuccessStatusCode();

        Assert.Equal(("moveUnfinishedTo", "invalid_sprint"), await ProjectKit.ProblemAsync(await SprintKit.CloseAsync(t.Owner, webSprint, wide)));
    }

    [Fact]
    public async Task The_quick_status_change_takes_a_known_status_from_a_worker_and_leaves_archived_issues_alone()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Moving");

        Assert.Equal(HttpStatusCode.NoContent, (await t.Contributor.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "in_progress" })).StatusCode);
        Assert.Equal("in_progress", await SprintKit.IssueStatusAsync(t.Owner, issue));
        Assert.Equal(("status", "invalid_value"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "finished" })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Reviewer.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "done" })).StatusCode);
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}")).EnsureSuccessStatusCode();
        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "done" })));
    }
}
