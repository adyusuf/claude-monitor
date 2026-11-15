using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Podium.Api.Tests.Infrastructure.JiraWriteSprintKit;

namespace Podium.Api.Tests;

/// <summary>The edges of mirrored sprint writes: mixing a Podium sprint with a mirrored one, a cleared date, and a close Jira half applied (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorSprintEdgeTests(ApiFactory api)
{
    [Fact]
    public async Task A_podium_only_sprint_cannot_close_into_a_mirrored_sprint_and_nothing_is_written()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var local = await s.LocalSprintAsync("Podium only");
        var localIssue = await s.LocalIssueAsync("Podium only issue");
        (await SprintKit.AddAsync(s.W.Member, local, localIssue)).EnsureSuccessStatusCode();
        await using (var db = api.Db())
        {
            await db.Sprints.Where(x => x.Id == local).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, SprintStatuses.Active));
        }

        var response = await SprintKit.CloseAsync(s.W.Member, local, await s.IdAsync("Plan A"));

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "mirrored_scope");
        Assert.Empty(s.W.Jira.Writes(s.W.Site));
        Assert.Equal("active", await SprintKit.StatusAsync(s.W.Member, local));
        Assert.Equal(local, await SprintKit.IssueSprintAsync(s.W.Member, localIssue));
    }

    [Fact]
    public async Task Clearing_a_date_of_a_mirrored_sprint_is_refused_and_nothing_changes_here_or_in_jira()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var id = await s.IdAsync("Plan A");

        var noEnd = await EditAsync(s.W.Member, id, new Dictionary<string, object?> { ["endDate"] = null });
        var noStart = await EditAsync(s.W.Member, id, new Dictionary<string, object?> { ["startDate"] = null, ["name"] = "Renamed too" });

        await JiraWriteKit.AssertRefusedAsync(noEnd, HttpStatusCode.Conflict, "mirrored_dates_kept");
        await JiraWriteKit.AssertRefusedAsync(noStart, HttpStatusCode.Conflict, "mirrored_dates_kept");
        Assert.Empty(s.W.Jira.Writes(s.W.Site));
        var row = await s.RowAsync("Plan A");
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 13)), (row.StartDate, row.EndDate));
        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(s.W.Member, await s.IdAsync("Plan B"), new { goal = "No dates in Jira either" })).StatusCode);
    }

    [Fact]
    public async Task A_close_whose_move_reached_jira_but_whose_close_was_refused_is_brought_in_line_by_the_next_pass()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var now = await s.IdAsync("Now");
        s.W.Site.Refuse(HttpMethod.Post, Route(s.Now), HttpStatusCode.BadRequest);

        await JiraWriteKit.AssertRefusedAsync(await SprintKit.CloseAsync(s.W.Member, now), HttpStatusCode.BadRequest, "tracker_rejected");

        Assert.Equal((null, null, "active"), (s.W.Site.Find("WRT-6")!.SprintId, s.W.Site.Find("WRT-7")!.SprintId, s.Now.State)); // Jira: moved, not closed
        Assert.Equal((now, now, "active"), (await s.SprintOfAsync("WRT-6"), await s.SprintOfAsync("WRT-7"), await SprintKit.StatusAsync(s.W.Member, now))); // here: unchanged
        await JiraKit.PassAsync(s.W.World);
        Assert.Equal((null, null, now, "active"), (await s.SprintOfAsync("WRT-6"), await s.SprintOfAsync("WRT-7"), await s.SprintOfAsync("WRT-8"), await SprintKit.StatusAsync(s.W.Member, now)));
    }
}
