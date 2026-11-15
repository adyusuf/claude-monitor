using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The link of an issue made in the tool is stamped by the application's clock, like every other write.</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueSyncedAtTests(ApiFactory api)
{
    [Fact]
    public async Task A_new_mirrored_issue_gets_a_link_synced_at_the_time_the_clock_gives()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        await IssueKit.Created(await w.Member.PostAsync($"/api/projects/{w.Project}/issues", new { title = "Clocked", type = "bug" }));

        var link = await w.LinkAsync((await w.RowAsync("WRT-5")).Id);
        Assert.Equal(api.Clock.GetUtcNow(), link.SyncedAt);
    }
}
