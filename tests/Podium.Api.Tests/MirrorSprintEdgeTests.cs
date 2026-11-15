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

}
