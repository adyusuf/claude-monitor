namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>A connected site with an imported space "SPR" that has four sprints and issues in each state (for the sprint tests).</summary>
public sealed record SprintWorld(
    JiraWorld World,
    FakeSpace Space,
    FakeSprint Closed,
    FakeSprint Active,
    FakeSprint Future,
    FakeSprint SecondActive,
    FakeIssue Done,
    FakeIssue Unfinished,
    FakeIssue Waiting,
    FakeIssue Ready,
    FakeIssue Working,
    FakeIssue Planned,
    FakeIssue Loose)
{
    public Guid Project { get; init; }
}

public static class JiraSprintKit
{
    /// <summary>
    /// Space SPR: a closed sprint (one done issue, one unfinished and one waiting issue), an active one (a waiting and a working issue), a future one
    /// (one waiting issue), a second active one (empty) and one issue in no sprint, and one person without an e-mail. The space is imported, the sprints are not.
    /// </summary>
    public static async Task<SprintWorld> ImportedAsync(ApiFactory api)
    {
        var w = await JiraKit.ConnectedAsync(api);
        var space = w.Site.Space("SPR", "Sprint space");
        var closed = w.Site.Sprint(space, "Sprint one", "closed", "2026-10-01", "2026-10-14");
        closed.Goal = "Warm up";
        var active = w.Site.Sprint(space, "Sprint two", "active", "2026-10-15", "2026-10-28");
        var future = w.Site.Sprint(space, "Sprint three", "future");
        var second = w.Site.Sprint(space, "Sprint four", "active", "2026-10-15", "2026-10-28");
        var done = space.Issue("Done in the closed one", i => { i.SprintId = closed.Id; i.Category = "done"; i.Status = "Done"; });
        var unfinished = space.Issue("Unfinished in the closed one", i => { i.SprintId = closed.Id; i.Category = "indeterminate"; i.Status = "In Progress"; });
        var waiting = space.Issue("Waiting in the closed one", i => i.SprintId = closed.Id);
        var ready = space.Issue("Waiting in the active one", i => i.SprintId = active.Id);
        var working = space.Issue("Working in the active one", i => { i.SprintId = active.Id; i.Category = "indeterminate"; i.Status = "In Progress"; });
        var planned = space.Issue("Planned in the future one", i => i.SprintId = future.Id);
        var loose = space.Issue("In no sprint");
        space.People.Add(new FakePerson("p-seed", "Seed Person"));
        await JiraKit.ImportAsync(w, "SPR");
        return new SprintWorld(w, space, closed, active, future, second, done, unfinished, waiting, ready, working, planned, loose) { Project = await JiraKit.ProjectIdAsync(w, "SPR") };
    }
}
