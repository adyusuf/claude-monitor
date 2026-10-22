using System.Net;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The Jira adapter over the scripted site (ADR-0015 §3): paging, the backlog and its fallback, the site's own field ids, and every "not there".</summary>
public sealed class JiraTrackerTests : IDisposable
{
    private readonly JiraClients stack = new();
    private readonly FakeSite site;
    private readonly FakeAccount account;
    private readonly TrackerContext ctx;

    public JiraTrackerTests()
    {
        site = stack.Jira.Site();
        account = stack.Jira.Account("Reader", null, site);
        ctx = stack.Context(site, account);
    }

    public void Dispose() => stack.Dispose();

    [Fact]
    public async Task Spaces_are_read_page_by_page_with_their_lead_and_description()
    {
        var lead = new FakePerson("lead-1", "Lead", "lead@example.invalid");
        for (var i = 0; i < 120; i++) site.Space($"S{i:D3}", $"Space {i}");
        site.Spaces[7].Lead = lead;
        site.Spaces[7].Description = "About it";
        site.Spaces[9].Deleted = true;

        var spaces = await stack.Tracker.ListContainersAsync(ctx, CancellationToken.None);

        Assert.Equal(119, spaces.Count);
        Assert.Equal(spaces.Count, spaces.Select(s => s.Key).Distinct().Count());
        var seven = spaces.Single(s => s.Key == "S007");
        Assert.Equal(("Space 7", "About it", "lead-1", "lead@example.invalid"), (seven.Name, seven.Description, seven.Lead!.Id, seven.Lead.Email));
        Assert.Equal(3, stack.Jira.Requests.Count(r => r.Path.EndsWith("/project/search", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_space_is_found_by_key_and_a_missing_one_is_null()
    {
        site.Space("ABC", "Alpha");

        Assert.Equal("Alpha", (await stack.Tracker.GetContainerAsync(ctx, "ABC", CancellationToken.None))!.Name);
        Assert.Null(await stack.Tracker.GetContainerAsync(ctx, "NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task Sprints_of_every_board_come_once_in_id_order_and_a_space_without_a_board_has_none()
    {
        var space = site.Space("ABC");
        var second = site.Sprint(space, "Second", "active");
        var first = site.Sprint(space, "First", "closed", "2026-10-01", "2026-10-14");
        site.Sprints[first.Id] = first;
        first.Goal = "Done";
        var gone = site.Sprint(space, "Deleted"); gone.Deleted = true;
        var other = site.Space("OTH");
        site.Sprint(other, "Other board");
        var none = site.Space("NOB", board: false);

        var sprints = await stack.Tracker.ListIterationsAsync(ctx, "ABC", CancellationToken.None);

        Assert.Equal([second.IdText, first.IdText], sprints.Select(s => s.Id)); // id order: created first, listed first
        Assert.Equal(("First", "closed", "Done", new DateOnly(2026, 10, 1)), (sprints[1].Name, sprints[1].State, sprints[1].Goal, sprints[1].Start));
        Assert.Empty(await stack.Tracker.ListIterationsAsync(ctx, none.Key, CancellationToken.None));
        Assert.Empty(await stack.Tracker.ListIterationsAsync(ctx, "MISSING", CancellationToken.None));
    }

    [Fact]
    public async Task The_backlog_is_the_boards_in_rank_order_over_several_pages_without_done_or_active_sprint_issues()
    {
        var space = site.Space("ABC");
        var active = site.Sprint(space, "Now", "active");
        for (var i = 0; i < 120; i++) space.Issue($"Backlog {i}");
        space.Issue("Done one", x => { x.Category = "done"; x.Status = "Done"; });
        space.Issue("In the sprint", x => x.SprintId = active.Id);
        space.Issue("Gone", x => x.Deleted = true);

        var backlog = await stack.Tracker.ListBacklogAsync(ctx, "ABC", CancellationToken.None);

        Assert.Equal(120, backlog.Count);
        Assert.Equal(Enumerable.Range(0, 120).Select(i => $"Backlog {i}"), backlog.Select(i => i.Summary));
        Assert.All(backlog, i => Assert.Equal("ABC", i.ContainerKey));
        Assert.Contains(stack.Jira.Requests, r => r.Path.Contains("/board/", StringComparison.Ordinal) && r.Path.EndsWith("/backlog", StringComparison.Ordinal));
        Assert.DoesNotContain(stack.Jira.Requests, r => r.Path.EndsWith("/search/jql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_space_without_a_board_or_with_a_board_that_has_no_backlog_falls_back_to_the_search()
    {
        var plain = site.Space("PLN", board: false);
        plain.Issue("Open one");
        plain.Issue("Closed one", x => { x.Category = "done"; });
        plain.Issue("In a sprint", x => x.SprintId = site.Sprint(plain, "S").Id);
        var kanban = site.Space("KAN");
        kanban.BoardListStatus = HttpStatusCode.BadRequest;
        kanban.Issue("Kanban card");

        var fromSearch = await stack.Tracker.ListBacklogAsync(ctx, "PLN", CancellationToken.None);
        var fromKanban = await stack.Tracker.ListBacklogAsync(ctx, "KAN", CancellationToken.None);

        Assert.Equal(["Open one"], fromSearch.Select(i => i.Summary));
        Assert.Equal(["Kanban card"], fromKanban.Select(i => i.Summary));
        Assert.Equal(2, stack.Jira.Requests.Count(r => r.Path.EndsWith("/search/jql", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_site_fields_for_sprint_and_story_points_are_found_once_and_used_to_read_issues()
    {
        var space = site.Space("ABC");
        var sprint = site.Sprint(space, "S1", "future");
        space.Issue("Pointed", x => { x.Points = 5; x.SprintId = sprint.Id; });
        space.Issue("Other", x => x.Points = 2);

        var fields = await stack.Tracker.FieldsAsync(ctx, CancellationToken.None);
        var backlog = await stack.Tracker.ListBacklogAsync(ctx, "ABC", CancellationToken.None);
        var item = await stack.Tracker.GetItemAsync(ctx, "ABC-1", CancellationToken.None);

        Assert.Equal(new JiraFields("customfield_10020", "customfield_10016"), fields);
        Assert.Equal(["5", "2"], backlog.Select(i => i.Estimate));
        Assert.Equal(sprint.IdText, item!.Iteration!.Id);
        Assert.Equal(1, stack.Jira.Requests.Count(r => r.Path.EndsWith("/rest/api/3/field", StringComparison.Ordinal)));
        Assert.Contains(stack.Jira.Requests, r => r.Path.Contains("/issue/ABC-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_issue_or_sprint_is_null_and_other_failures_pass_on_with_their_code()
    {
        site.Space("ABC").Issue("Exists");

        Assert.Equal("Exists", (await stack.Tracker.GetItemAsync(ctx, "ABC-1", CancellationToken.None))!.Summary);
        Assert.Null(await stack.Tracker.GetItemAsync(ctx, "ABC-99", CancellationToken.None));
        Assert.Null(await stack.Tracker.GetIterationAsync(ctx, "424242", CancellationToken.None));
        stack.Jira.Fail(site, "/issue/ABC-1", HttpStatusCode.Forbidden);
        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.Tracker.GetItemAsync(ctx, "ABC-1", CancellationToken.None));
        Assert.Equal(TrackerException.Codes.Forbidden, e.Code);
    }

    [Fact]
    public async Task A_sprint_is_read_with_its_issues_and_a_deleted_one_is_null()
    {
        var space = site.Space("ABC");
        var sprint = site.Sprint(space, "S1", "active", "2026-11-02", "2026-11-13");
        space.Issue("In", x => x.SprintId = sprint.Id);
        space.Issue("Also in", x => { x.SprintId = sprint.Id; x.Category = "done"; });
        space.Issue("Out");
        var gone = site.Sprint(space, "Gone"); gone.Deleted = true;

        var found = await stack.Tracker.GetIterationAsync(ctx, sprint.IdText, CancellationToken.None);
        var items = await stack.Tracker.ListIterationItemsAsync(ctx, sprint.IdText, CancellationToken.None);

        Assert.Equal(("S1", "active", new DateOnly(2026, 11, 13)), (found!.Name, found.State, found.End));
        Assert.Equal(["In", "Also in"], items.Select(i => i.Summary));
        Assert.Equal(sprint.IdText, items[0].Iteration!.Id);
        Assert.Null(await stack.Tracker.GetIterationAsync(ctx, gone.IdText, CancellationToken.None));
    }

    [Fact]
    public async Task Changed_issues_come_from_a_search_since_a_minute_in_utc_and_nothing_is_asked_for_no_space()
    {
        var space = site.Space("ABC");
        var other = site.Space("OTH");
        space.Issue("Fresh");
        other.Issue("Other fresh");
        space.Issue("Stale").Updated = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var since = new DateTimeOffset(2026, 1, 1, 8, 30, 45, TimeSpan.FromHours(2));

        var changed = await stack.Tracker.ListChangedAsync(ctx, ["ABC"], since, CancellationToken.None);
        var both = await stack.Tracker.ListChangedAsync(ctx, ["ABC", "OTH"], since, CancellationToken.None);

        Assert.Equal(["Fresh"], changed.Select(i => i.Summary));
        Assert.Equal(["Fresh", "Other fresh"], both.Select(i => i.Summary).Order());
        var jql = System.Text.Json.JsonDocument.Parse(stack.Jira.Requests.First(r => r.Path.EndsWith("/search/jql", StringComparison.Ordinal)).Body).RootElement.GetProperty("jql").GetString();
        Assert.Equal("project in (\"ABC\") AND updated >= \"2026-01-01 06:30\" ORDER BY updated ASC", jql); // the minute, in UTC
        stack.Jira.Requests.Clear();
        Assert.Empty(await stack.Tracker.ListChangedAsync(ctx, [], since, CancellationToken.None));
        Assert.Empty(stack.Jira.Requests);
    }

    [Fact]
    public async Task Every_id_of_a_space_is_listed_over_several_pages_and_a_quote_in_a_key_cannot_break_the_search()
    {
        var space = site.Space("ABC");
        for (var i = 0; i < 230; i++) space.Issue("Issue " + i);
        space.Issue("Deleted", x => x.Deleted = true);
        site.Space("OTH").Issue("Other");

        var ids = await stack.Tracker.ListItemIdsAsync(ctx, "ABC", CancellationToken.None);
        var e = await Assert.ThrowsAsync<TrackerException>(() => stack.Tracker.ListItemIdsAsync(ctx, "A\" OR project = \"OTH", CancellationToken.None));

        Assert.Equal(space.Issues.Where(i => !i.Deleted).Select(i => i.Id).Order(), ids.Order());
        Assert.Equal(TrackerException.Codes.Rejected, e.Code); // the fake accepts only the three queries the code sends: the key stayed inside its quotes
    }

    [Fact]
    public async Task People_come_from_the_assignable_search_without_duplicates_over_several_pages()
    {
        var space = site.Space("ABC");
        for (var i = 0; i < 70; i++) space.People.Add(new FakePerson($"p-{i}", $"Person {i}", i % 2 == 0 ? $"p{i}@example.invalid" : null));
        space.People.Add(new FakePerson("bot", "Robot") { AccountType = "app" });

        var people = await stack.Tracker.ListPeopleAsync(ctx, "ABC", CancellationToken.None);

        Assert.Equal(71, people.Count);
        Assert.Equal(35, people.Count(p => p.Email is not null));
        Assert.False(people.Single(p => p.Id == "bot").Active);
        Assert.Empty(await stack.Tracker.ListPeopleAsync(ctx, "EMPTY", CancellationToken.None));
    }

    [Fact]
    public void The_adapter_says_which_tool_it_is()
    {
        Assert.Equal("jira", stack.Tracker.Provider);
        Assert.Equal(["jira"], TrackerProviders.All);
    }
}
