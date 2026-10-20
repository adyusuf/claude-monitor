using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What an imported sprint and its issues look like in Podium (ADR-0015 §4): states, membership, numbers, and the rules about active sprints.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationSprintMirrorTests(ApiFactory api)
{
    private static async Task<(Dictionary<string, Sprint> Sprints, Dictionary<string, Issue> Issues)> ReadAsync(SprintWorld s)
    {
        var sprints = (await JiraKit.SprintsAsync(s.World, s.Project)).ToDictionary(x => x.Name!);
        var issues = (await JiraKit.IssuesAsync(s.World, s.Project)).ToDictionary(x => x.Title);
        return (sprints, issues);
    }

    [Fact]
    public async Task Planned_active_and_closed_sprints_keep_their_name_goal_dates_and_numbers_in_the_order_asked()
    {
        var s = await JiraSprintKit.ImportedAsync(api);

        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id, s.Future.Id, s.Active.Id);

        var sprints = await JiraKit.SprintsAsync(s.World, s.Project);
        Assert.Equal([(1, "Sprint one", "closed"), (2, "Sprint three", "planned"), (3, "Sprint two", "active")], sprints.Select(x => (x.Number, x.Name!, x.Status)));
        var closed = sprints[0];
        Assert.Equal(("Warm up", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14), s.World.ConnectionId), (closed.Goal, closed.StartDate, closed.EndDate, closed.SourceConnectionId));
        Assert.All(sprints.Where(x => x.Status != SprintStatuses.Planned), x => Assert.NotNull(x.StartedAt));
        Assert.NotNull(closed.ClosedAt);
        Assert.Null(sprints[1].StartedAt);
        Assert.Null(sprints[1].ClosedAt);
        Assert.Null(sprints[2].ClosedAt);
        Assert.All(sprints, x => Assert.Equal((s.World.Admin.Id, s.Project, null), (x.CreatedBy, x.ProjectId, (Guid?)x.ApplicationId)));
        // Numbers count per project: another space of the same site starts again at 1.
        var other = s.World.Site.Space("SP2");
        var lone = s.World.Site.Sprint(other, "Lone", "future");
        await JiraKit.ImportAsync(s.World, "SP2");
        await JiraKit.ImportSprintsAsync(s.World, "SP2", lone.Id);
        Assert.Equal([1], (await JiraKit.SprintsAsync(s.World, await JiraKit.ProjectIdAsync(s.World, "SP2"))).Select(x => x.Number));
    }

    [Fact]
    public async Task Odd_sprint_data_is_cleaned_an_end_before_the_start_a_long_name_and_a_long_goal()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var odd = s.World.Site.Sprint(s.Space, new string('n', 150), "future", "2026-11-13", "2026-11-02");
        odd.Goal = new string('g', 1500);

        await JiraKit.ImportSprintsAsync(s.World, "SPR", odd.Id);

        var sprint = Assert.Single(await JiraKit.SprintsAsync(s.World, s.Project));
        Assert.Equal((100, 1000, new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 13)), (sprint.Name!.Length, sprint.Goal!.Length, sprint.StartDate, sprint.EndDate));
    }

    [Fact]
    public async Task The_issues_land_in_their_sprints_and_an_unfinished_issue_of_a_closed_sprint_is_back_in_the_backlog()
    {
        var s = await JiraSprintKit.ImportedAsync(api);

        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id, s.Active.Id, s.Future.Id);

        var (sprints, issues) = await ReadAsync(s);
        Guid? Sprint(string name) => sprints[name].Id;
        Assert.Equal((Sprint("Sprint one"), "done"), (issues[s.Done.Summary].SprintId, issues[s.Done.Summary].Status)); // finished work stays in its sprint
        Assert.Equal((null, "backlog"), (issues[s.Waiting.Summary].SprintId, issues[s.Waiting.Summary].Status)); // not ready: it is in no sprint
        Assert.Equal((null, "in_progress"), (issues[s.Unfinished.Summary].SprintId, issues[s.Unfinished.Summary].Status));
        Assert.Equal((Sprint("Sprint two"), "ready"), (issues[s.Ready.Summary].SprintId, issues[s.Ready.Summary].Status));
        Assert.Equal((Sprint("Sprint two"), "in_progress"), (issues[s.Working.Summary].SprintId, issues[s.Working.Summary].Status));
        Assert.Equal((Sprint("Sprint three"), "ready"), (issues[s.Planned.Summary].SprintId, issues[s.Planned.Summary].Status)); // moved in from the backlog
        Assert.Equal((null, "backlog"), (issues[s.Loose.Summary].SprintId, issues[s.Loose.Summary].Status));
        Assert.Equal(7, issues.Count); // no issue twice: the ones imported with the space were found again
        Assert.Equal(issues.Values.Select(i => i.Number).Order(), Enumerable.Range(1, 7));
        var sprint = (await s.World.Admin.GetJsonAsync($"/api/sprints/{sprints["Sprint two"].Id}")).GetProperty("sprint");
        Assert.Equal(("active", 2), (sprint.GetProperty("status").GetString(), sprint.GetProperty("issueCount").GetInt32()));
    }

    [Fact]
    public async Task Two_sprints_active_at_once_are_both_accepted_but_a_second_sprint_made_here_is_not()
    {
        var s = await JiraSprintKit.ImportedAsync(api);

        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Active.Id, s.SecondActive.Id);

        Assert.Equal(["active", "active"], (await JiraKit.SprintsAsync(s.World, s.Project)).Select(x => x.Status));
        // The one-active-sprint rule still holds for sprints that Podium owns, in a project made here (the rule through the API)...
        var team = await IssueKit.TeamAsync(api);
        var one = await SprintKit.SprintAsync(team.Owner, team.Project);
        var two = await SprintKit.SprintAsync(team.Owner, team.Project);
        (await SprintKit.StartAsync(team.Owner, one)).EnsureSuccessStatusCode();
        Assert.Equal(("status", "sprint_active"), await ProjectKit.ProblemAsync(await SprintKit.StartAsync(team.Owner, two)));
        // ... and in the database, next to the mirrored active sprints of an imported project.
        await using var db = api.Db();
        db.Sprints.Add(NativeActive(s, 90));
        await db.SaveChangesAsync();
        db.Sprints.Add(NativeActive(s, 91));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static Sprint NativeActive(SprintWorld s, int number) => new()
    {
        WorkspaceId = s.World.WorkspaceId,
        ProjectId = s.Project,
        Number = number,
        Status = SprintStatuses.Active,
        StartedAt = DateTimeOffset.UtcNow,
        CreatedBy = s.World.Admin.Id,
        UpdatedBy = s.World.Admin.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Importing_the_same_sprints_again_adds_no_row_and_moves_an_issue_that_moved_in_jira()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id, s.Active.Id, s.Future.Id);
        var (sprintsBefore, issuesBefore) = await ReadAsync(s);

        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id, s.Active.Id, s.Future.Id);
        var (sprintsSame, issuesSame) = await ReadAsync(s);
        s.Ready.SprintId = s.Future.Id; // moved in Jira
        api.Jira.Touch(s.Ready);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Future.Id, s.Active.Id);
        var (sprintsAfter, issuesAfter) = await ReadAsync(s);

        Assert.Equal(sprintsBefore.Values.Select(x => x.Id).Order(), sprintsSame.Values.Select(x => x.Id).Order());
        Assert.Equal(issuesBefore.Values.Select(x => x.Id).Order(), issuesSame.Values.Select(x => x.Id).Order());
        Assert.Equal(issuesBefore.Values.Select(x => x.Position).Order(), issuesSame.Values.Select(x => x.Position).Order());
        Assert.Equal(sprintsAfter["Sprint three"].Id, issuesAfter[s.Ready.Summary].SprintId);
        Assert.Equal(issuesBefore[s.Ready.Summary].Id, issuesAfter[s.Ready.Summary].Id);
        await using var db = api.Db();
        var runs = await db.IntegrationSyncRuns.AsNoTracking().Where(r => r.ConnectionId == s.World.ConnectionId && r.Kind == SyncKinds.Import).ToListAsync();
        Assert.Contains(runs, r => r.Created == 0 && r.Updated == 3 && r.Status == SyncStatuses.Ok); // the second run: the three sprints, no new row
    }

    [Fact]
    public async Task An_epic_of_a_sprint_issue_that_is_in_no_list_is_fetched_on_its_own()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var space = w.Site.Space("EPS");
        var sprint = w.Site.Sprint(space, "Only", "active", "2026-10-15", "2026-10-28");
        space.Issue("Hidden epic", i => { i.Type = "Epic"; i.Category = "done"; i.Status = "Done"; });
        space.Issue("Child in sprint", i => { i.ParentKey = "EPS-1"; i.SprintId = sprint.Id; });
        await JiraKit.ImportAsync(w, "EPS");
        Assert.Empty(await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "EPS"))); // the backlog had neither

        await JiraKit.ImportSprintsAsync(w, "EPS", sprint.Id);

        var issues = (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "EPS"))).ToDictionary(i => i.Title);
        Assert.Equal(["Child in sprint", "Hidden epic"], issues.Keys.Order());
        Assert.Equal(issues["Hidden epic"].Id, issues["Child in sprint"].ParentId);
        Assert.Equal(1, issues["Hidden epic"].Number); // epics first
    }
}
