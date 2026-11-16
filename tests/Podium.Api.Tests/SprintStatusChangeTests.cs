using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The quick status change and the state of the sprint the issue is in: a closed sprint is read-only.</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintStatusChangeTests(ApiFactory api)
{
    [Fact]
    public async Task An_issue_in_a_closed_sprint_keeps_its_status_while_one_in_a_planned_or_active_sprint_may_change()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var done = await IssueKit.IssueAsync(t.Owner, t.Project, "Finished", "story", new { status = "done" });
        (await SprintKit.AddAsync(t.Owner, sprint, done)).EnsureSuccessStatusCode();
        Task<HttpResponseMessage> Status(string status) => t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{done}/status", new { status });

        Assert.Equal(HttpStatusCode.NoContent, (await Status("in_review")).StatusCode);
        (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await Status("done")).StatusCode);
        (await SprintKit.CloseAsync(t.Owner, sprint)).EnsureSuccessStatusCode();

        Assert.Equal(("status", "closed"), await ProjectKit.ProblemAsync(await Status("in_progress")));
        Assert.Equal("done", await SprintKit.IssueStatusAsync(t.Owner, done));
    }

    [Fact]
    public async Task An_issue_in_a_sprint_keeps_the_status_backlog_it_came_with_and_may_be_set_back_to_it_and_stays_in_the_sprint()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var waiting = await IssueKit.IssueAsync(t.Owner, t.Project, "Not ready yet", "story", new { status = "backlog" });
        var working = await IssueKit.IssueAsync(t.Owner, t.Project, "In flight", "story", new { status = "in_progress" });
        (await SprintKit.AddAsync(t.Owner, sprint, waiting, working)).EnsureSuccessStatusCode();

        Assert.Equal(("backlog", sprint), (await SprintKit.IssueStatusAsync(t.Owner, waiting), await SprintKit.IssueSprintAsync(t.Owner, waiting)));
        (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        var back = await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{working}/status", new { status = "backlog" });

        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        Assert.Equal(("backlog", sprint), (await SprintKit.IssueStatusAsync(t.Owner, working), await SprintKit.IssueSprintAsync(t.Owner, working)));
        var counts = (await SprintKit.ViewAsync(t.Owner, sprint)).GetProperty("statusCounts");
        Assert.Equal((2, 0), (counts.GetProperty("backlog").GetInt32(), counts.GetProperty("in_progress").GetInt32()));
        Assert.Empty(await IssueKit.TitlesAsync(t.Owner, $"/api/projects/{t.Project}/issues?scope=backlog"));
    }
}
