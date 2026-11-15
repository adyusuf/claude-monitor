using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>What the sync does with the issues of a mirrored sprint that was deleted in the tool (ADR-0015 §4).</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintMirrorArchiveTests(ApiFactory api)
{
    [Fact]
    public async Task A_deleted_mirrored_sprint_sends_its_issues_to_the_backlog_and_they_keep_the_tools_status()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Active.Id);
        var before = await JiraKit.IssueAsync(s.World, s.Working.Key);
        Assert.Equal(("in_progress", true), (before.Status, before.SprintId is not null));
        // Nothing about the issues changes in the tool (no new stamp): only the sprint is gone, so the pass archives it and reads no issue.
        api.Clock.Advance(TimeSpan.FromHours(2));
        await JiraKit.PassAsync(s.World);
        s.Active.Deleted = true;

        await JiraKit.PassAsync(s.World);

        Assert.NotNull(Assert.Single(await JiraKit.SprintsAsync(s.World, s.Project)).ArchivedAt);
        var working = await JiraKit.IssueAsync(s.World, s.Working.Key);
        Assert.Equal((null, "in_progress"), (working.SprintId, working.Status));
        var waiting = await JiraKit.IssueAsync(s.World, s.Ready.Key);
        Assert.Equal((null, "ready"), (waiting.SprintId, waiting.Status));
    }
}
