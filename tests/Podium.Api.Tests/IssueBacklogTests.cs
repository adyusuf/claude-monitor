using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The backlog of a project: its order, the moves, the filters and the paging (ADR-0011 §4).</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueBacklogTests(ApiFactory api)
{
    private static string Url(IssueTeam t, string query = "") => $"/api/projects/{t.Project}/issues{query}";

    [Fact]
    public async Task New_issues_go_to_the_end_of_the_backlog_and_a_move_puts_one_on_top_or_after_another()
    {
        var t = await IssueKit.TeamAsync(api);
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A");
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B");
        var c = await IssueKit.IssueAsync(t.Owner, t.Project, "C");
        var d = await IssueKit.IssueAsync(t.Owner, t.Project, "D");
        Assert.Equal(["A", "B", "C", "D"], await IssueKit.TitlesAsync(t.Owner, Url(t)));

        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{d}/position", new { afterIssueId = (Guid?)null })).EnsureSuccessStatusCode();
        Assert.Equal(["D", "A", "B", "C"], await IssueKit.TitlesAsync(t.Owner, Url(t)));
        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{d}/position", new { afterIssueId = b })).EnsureSuccessStatusCode();
        Assert.Equal(["A", "B", "D", "C"], await IssueKit.TitlesAsync(t.Owner, Url(t)));
        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{a}/position", new { afterIssueId = c })).EnsureSuccessStatusCode();
        Assert.Equal(["B", "D", "C", "A"], await IssueKit.TitlesAsync(t.Owner, Url(t)));
    }

    [Fact]
    public async Task A_move_after_an_issue_of_another_project_or_an_unknown_one_is_refused()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var mine = await IssueKit.IssueAsync(t.Owner, t.Project, "Mine");
        var foreign = await IssueKit.IssueAsync(other.Owner, other.Project, "Foreign");

        Assert.Equal(("afterIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{mine}/position", new { afterIssueId = foreign })));
        Assert.Equal(("afterIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{mine}/position", new { afterIssueId = Guid.NewGuid() })));
    }

    [Fact]
    public async Task When_two_neighbours_have_no_room_left_the_positions_are_spaced_again_and_the_order_still_holds()
    {
        var t = await IssueKit.TeamAsync(api);
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A");
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B");
        var c = await IssueKit.IssueAsync(t.Owner, t.Project, "C");
        await using (var db = api.Db())
        {
            await db.Issues.Where(i => i.Id == a).ExecuteUpdateAsync(s => s.SetProperty(i => i.Position, 1.0));
            await db.Issues.Where(i => i.Id == b).ExecuteUpdateAsync(s => s.SetProperty(i => i.Position, Math.BitIncrement(1.0)));
            await db.Issues.Where(i => i.Id == c).ExecuteUpdateAsync(s => s.SetProperty(i => i.Position, 5.0));
        }

        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{c}/position", new { afterIssueId = a })).EnsureSuccessStatusCode();

        Assert.Equal(["A", "C", "B"], await IssueKit.TitlesAsync(t.Owner, Url(t)));
    }

    [Fact]
    public async Task The_backlog_scope_filters_and_the_search_narrow_the_list_without_case_or_accent()
    {
        var t = await IssueKit.TeamAsync(api);
        await IssueKit.IssueAsync(t.Owner, t.Project, "İstanbul map", "story", new { status = "backlog", assigneeUserId = t.Contributor.Id });
        await IssueKit.IssueAsync(t.Owner, t.Project, "Ready one", "story", new { status = "ready", applicationId = t.Application, componentId = t.Component, releaseId = t.Release });
        await IssueKit.IssueAsync(t.Owner, t.Project, "Doing", "bug", new { status = "in_progress", severity = "minor" });
        var epic = await IssueKit.IssueAsync(t.Owner, t.Project, "Big epic", "epic", new { status = "done" });
        await IssueKit.IssueAsync(t.Owner, t.Project, "Under epic", "story", new { parentId = epic });

        Assert.Equal(["İstanbul map", "Ready one", "Under epic"], await IssueKit.TitlesAsync(t.Owner, Url(t, "?scope=backlog")));
        Assert.Equal(["İstanbul map"], await IssueKit.TitlesAsync(t.Owner, Url(t, "?q=ISTANBUL")));
        Assert.Equal(["Doing"], await IssueKit.TitlesAsync(t.Owner, Url(t, "?type=bug")));
        Assert.Equal(["Doing"], await IssueKit.TitlesAsync(t.Owner, Url(t, "?status=in_progress")));
        Assert.Equal(["İstanbul map"], await IssueKit.TitlesAsync(t.Owner, Url(t, $"?assignee={t.Contributor.Id}")));
        Assert.Equal(["Ready one"], await IssueKit.TitlesAsync(t.Owner, Url(t, $"?application={t.Application}")));
        Assert.Equal(["Ready one"], await IssueKit.TitlesAsync(t.Owner, Url(t, $"?component={t.Component}")));
        Assert.Equal(["Ready one"], await IssueKit.TitlesAsync(t.Owner, Url(t, $"?release={t.Release}")));
        Assert.Equal(["Under epic"], await IssueKit.TitlesAsync(t.Owner, Url(t, $"?parent={epic}")));
    }

    [Fact]
    public async Task Archived_issues_leave_the_list_and_show_under_the_archived_switch()
    {
        var t = await IssueKit.TeamAsync(api);
        await IssueKit.IssueAsync(t.Owner, t.Project, "Stays");
        var gone = await IssueKit.IssueAsync(t.Owner, t.Project, "Goes");
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{gone}")).EnsureSuccessStatusCode();

        Assert.Equal(["Stays"], await IssueKit.TitlesAsync(t.Reviewer, Url(t)));
        Assert.Equal(["Goes"], await IssueKit.TitlesAsync(t.Reviewer, Url(t, "?archived=true")));
    }

    [Fact]
    public async Task The_list_pages_in_backlog_order_and_a_bad_cursor_starts_from_the_top()
    {
        var t = await IssueKit.TeamAsync(api);
        foreach (var title in new[] { "1", "2", "3", "4", "5" }) await IssueKit.IssueAsync(t.Owner, t.Project, title);

        var first = await t.Owner.GetJsonAsync(Url(t, "?limit=2"));
        var second = await t.Owner.GetJsonAsync(Url(t, $"?limit=2&cursor={first.GetProperty("next").GetString()}"));
        var last = await t.Owner.GetJsonAsync(Url(t, $"?limit=2&cursor={second.GetProperty("next").GetString()}"));

        Assert.Equal(["1", "2"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(["3", "4"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(["5"], last.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);
        Assert.Equal(["1", "2"], (await t.Owner.GetJsonAsync(Url(t, "?limit=2&cursor=garbage"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task A_stranger_gets_no_list_and_a_viewer_reads_it()
    {
        var t = await IssueKit.TeamAsync(api);
        await IssueKit.IssueAsync(t.Owner, t.Project, "Visible");
        var stranger = await api.NewClient().SignedUpAsync("bl-stranger");

        Assert.Equal(["Visible"], await IssueKit.TitlesAsync(t.Viewer, Url(t)));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync(Url(t))).StatusCode);
    }
}
