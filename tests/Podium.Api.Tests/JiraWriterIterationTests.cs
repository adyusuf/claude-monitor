using System.Net;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The Jira write adapter for sprints (ADR-0015 §7): the board a sprint is made on, and exactly what each change puts on the wire.</summary>
public sealed class JiraWriterIterationTests : IDisposable
{
    private readonly JiraClients stack = new();
    private readonly FakeSite site;
    private readonly FakeSpace space;
    private readonly TrackerContext ctx;

    public JiraWriterIterationTests()
    {
        site = stack.Jira.Site();
        space = site.Space("ABC");
        ctx = stack.Context(site, stack.Jira.Account("Writer", null, site));
    }

    public void Dispose() => stack.Dispose();

    private static string Route(FakeSprint sprint) => $"/rest/agile/1.0/sprint/{sprint.Id}";

    [Fact]
    public async Task A_sprint_is_made_on_the_first_board_of_the_space_and_comes_back_as_the_tools_sprint()
    {
        var made = await stack.Writer.CreateIterationAsync(ctx, "ABC", new IterationChange("Sprint 9", "Ship it", null, null), CancellationToken.None);

        var body = stack.Jira.Sent(site, HttpMethod.Post, "/rest/agile/1.0/sprint");
        Assert.Equal(["goal", "name", "originBoardId"], body.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("Sprint 9", "Ship it", space.BoardId), (body["name"]!.GetValue<string>(), body["goal"]!.GetValue<string>(), body["originBoardId"]!.GetValue<long?>()));
        var sprint = site.Sprints.Values.Single();
        Assert.Equal((sprint.IdText, "Sprint 9", "future", "Ship it", "ABC"), (made!.Id, made.Name, made.State, made.Goal, made.ContainerKey));
    }

    [Fact]
    public async Task A_space_without_a_board_gets_no_sprint_and_no_call_is_made_to_make_one()
    {
        site.Space("NOB", board: false);

        var made = await stack.Writer.CreateIterationAsync(ctx, "NOB", new IterationChange("Sprint 9", null, null, null), CancellationToken.None);

        Assert.Null(made);
        Assert.Empty(stack.Jira.Writes(site));
        await Assert.ThrowsAsync<ArgumentNullException>(() => stack.Writer.CreateIterationAsync(ctx, "ABC", null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_sprints_dates_go_as_iso_date_times_at_midnight_utc()
    {
        await stack.Writer.CreateIterationAsync(ctx, "ABC", new IterationChange("Dated", null, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 13)), CancellationToken.None);

        var body = stack.Jira.Sent(site, HttpMethod.Post, "/rest/agile/1.0/sprint");
        Assert.Equal(("2026-11-02T00:00:00.000Z", "2026-11-13T00:00:00.000Z"), (body["startDate"]!.GetValue<string>(), body["endDate"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_update_sends_only_the_values_given_and_an_empty_goal_is_sent_as_it_is()
    {
        var sprint = site.Sprint(space, "Old", "future");
        sprint.Goal = "old goal";

        await stack.Writer.UpdateIterationAsync(ctx, sprint.IdText, new IterationChange("New", null, null, null), CancellationToken.None);
        await stack.Writer.UpdateIterationAsync(ctx, sprint.IdText, new IterationChange(null, "", null, null), CancellationToken.None);

        var bodies = stack.Jira.Bodies(site, HttpMethod.Post, Route(sprint));
        Assert.Equal(["name"], bodies[0].Select(f => f.Key));
        Assert.Equal(("goal", ""), (bodies[1].Single().Key, bodies[1]["goal"]!.GetValue<string>()));
        Assert.Equal(("New", ""), (sprint.Name, sprint.Goal));
    }

    [Fact]
    public async Task Starting_sends_the_active_state_with_both_dates_and_the_sprint_becomes_active()
    {
        var sprint = site.Sprint(space, "Go", "future");

        await stack.Writer.StartIterationAsync(ctx, sprint.IdText, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 13), CancellationToken.None);

        var body = stack.Jira.Sent(site, HttpMethod.Post, Route(sprint));
        Assert.Equal(("active", "2026-11-02T00:00:00.000Z", "2026-11-13T00:00:00.000Z"), (body["state"]!.GetValue<string>(), body["startDate"]!.GetValue<string>(), body["endDate"]!.GetValue<string>()));
        Assert.Equal("active", sprint.State);
    }

    [Fact]
    public async Task Closing_sends_only_the_closed_state_and_deleting_is_a_delete_of_the_sprint()
    {
        var closing = site.Sprint(space, "Running", "active", "2026-10-01", "2026-10-14");
        var deleting = site.Sprint(space, "Planned", "future");

        await stack.Writer.CloseIterationAsync(ctx, closing.IdText, CancellationToken.None);
        await stack.Writer.DeleteIterationAsync(ctx, deleting.IdText, CancellationToken.None);

        var body = stack.Jira.Sent(site, HttpMethod.Post, Route(closing));
        Assert.Equal(("state", "closed"), (body.Single().Key, body["state"]!.GetValue<string>()));
        Assert.Equal(("closed", true), (closing.State, deleting.Deleted));
        Assert.Equal(["DELETE " + Route(deleting)], stack.Jira.Writes(site).Where(c => c.StartsWith("DELETE", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Jiras_refusals_come_out_as_short_codes_with_none_of_its_text()
    {
        var planned = site.Sprint(space, "Planned", "future");
        var closed = site.Sprint(space, "Past", "closed", "2026-09-01", "2026-09-14");

        var cannotClose = await Assert.ThrowsAsync<TrackerException>(() => stack.Writer.CloseIterationAsync(ctx, planned.IdText, CancellationToken.None));
        var cannotChange = await Assert.ThrowsAsync<TrackerException>(() => stack.Writer.UpdateIterationAsync(ctx, closed.IdText, new IterationChange("x", null, null, null), CancellationToken.None));
        var missing = await Assert.ThrowsAsync<TrackerException>(() => stack.Writer.DeleteIterationAsync(ctx, "999999", CancellationToken.None));
        site.Refuse(HttpMethod.Delete, Route(planned), HttpStatusCode.Forbidden);
        var forbidden = await Assert.ThrowsAsync<TrackerException>(() => stack.Writer.DeleteIterationAsync(ctx, planned.IdText, CancellationToken.None));

        Assert.Equal([TrackerException.Codes.Rejected, TrackerException.Codes.Rejected, TrackerException.Codes.NotFound, TrackerException.Codes.Forbidden],
            [cannotClose.Code, cannotChange.Code, missing.Code, forbidden.Code]);
        Assert.DoesNotContain("secret detail", cannotClose.Message + forbidden.Message, StringComparison.Ordinal);
    }
}
