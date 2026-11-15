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

}
