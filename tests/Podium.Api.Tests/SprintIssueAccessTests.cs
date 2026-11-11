using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Who may put issues into a sprint and take them out, and that nothing from another workspace can be reached through it (ADR-0011 §2, §5).</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintIssueAccessTests(ApiFactory api)
{
    [Fact]
    public async Task Adding_issues_is_refused_to_a_workspace_viewer_with_403_and_nothing_is_added()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Waiting");

        var response = await SprintKit.AddAsync(t.Viewer, sprint, issue);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, issue));
    }

    [Fact]
    public async Task Adding_issues_is_refused_to_a_project_reviewer_with_403_and_nothing_is_added()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Waiting");

        var response = await SprintKit.AddAsync(t.Reviewer, sprint, issue);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, issue));
    }

    [Fact]
    public async Task Removing_an_issue_is_refused_to_a_workspace_viewer_with_403_and_the_issue_stays_in_the_sprint()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Planned");
        (await SprintKit.AddAsync(t.Owner, sprint, issue)).EnsureSuccessStatusCode();

        var response = await t.Viewer.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{issue}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(sprint, await SprintKit.IssueSprintAsync(t.Owner, issue));
    }

    [Fact]
    public async Task Adding_issues_to_a_sprint_of_another_workspace_answers_404_and_nothing_is_added()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("sa-stranger");
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Waiting");

        var response = await SprintKit.AddAsync(stranger, sprint, issue);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, issue));
    }

    [Fact]
    public async Task Removing_an_issue_from_a_sprint_of_another_workspace_answers_404_and_the_issue_stays_in_the_sprint()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("sa-stranger");
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Planned");
        (await SprintKit.AddAsync(t.Owner, sprint, issue)).EnsureSuccessStatusCode();

        var response = await stranger.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{issue}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(sprint, await SprintKit.IssueSprintAsync(t.Owner, issue));
    }

    [Fact]
    public async Task Adding_an_issue_of_another_workspace_is_refused_as_an_invalid_issue_and_that_issue_is_untouched()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var foreign = await IssueKit.IssueAsync(other.Owner, other.Project, "Foreign");

        var response = await SprintKit.AddAsync(t.Owner, sprint, foreign);

        Assert.Equal(("issueIds", "invalid_issue"), await ProjectKit.ProblemAsync(response));
        Assert.Null(await SprintKit.IssueSprintAsync(other.Owner, foreign));
    }

    [Fact]
    public async Task Closing_with_a_planned_sprint_of_another_workspace_as_target_is_refused_and_the_sprint_stays_active()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var current = await SprintKit.SprintAsync(t.Owner, t.Project);
        var foreignTarget = await SprintKit.SprintAsync(other.Owner, other.Project);
        var waiting = await IssueKit.IssueAsync(t.Owner, t.Project, "Waiting");
        (await SprintKit.AddAsync(t.Owner, current, waiting)).EnsureSuccessStatusCode();
        (await SprintKit.StartAsync(t.Owner, current)).EnsureSuccessStatusCode();

        var response = await SprintKit.CloseAsync(t.Owner, current, foreignTarget);

        Assert.Equal(("moveUnfinishedTo", "invalid_sprint"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(("active", current), (await SprintKit.StatusAsync(t.Owner, current), await SprintKit.IssueSprintAsync(t.Owner, waiting)));
    }

    [Fact]
    public async Task Closing_with_a_planned_sprint_of_another_project_in_the_same_workspace_is_refused()
    {
        var t = await IssueKit.TeamAsync(api);
        var otherProject = (await ProjectKit.CreateProjectAsync(t.Owner, "Second tracker")).GetProperty("id").GetGuid();
        var current = await SprintKit.SprintAsync(t.Owner, t.Project);
        var elsewhere = await SprintKit.SprintAsync(t.Owner, otherProject);
        (await SprintKit.StartAsync(t.Owner, current)).EnsureSuccessStatusCode();

        var response = await SprintKit.CloseAsync(t.Owner, current, elsewhere);

        Assert.Equal(("moveUnfinishedTo", "invalid_sprint"), await ProjectKit.ProblemAsync(response));
    }
}
