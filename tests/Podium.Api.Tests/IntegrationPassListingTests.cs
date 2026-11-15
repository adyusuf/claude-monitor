using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// A pass whose sprint listing came back short because Jira refused a board or sprint list (which the tracker reads as "none") does not take that
/// for deleted sprints: an imported sprint and its issues stay as they were, and the next whole listing applies again.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPassListingTests(ApiFactory api)
{
    [Theory]
    [InlineData("/rest/agile/1.0/board?projectKeyOrId", HttpStatusCode.Forbidden)]
    [InlineData("/rest/agile/1.0/board?projectKeyOrId", HttpStatusCode.NotFound)]
    [InlineData("/sprint?maxResults", HttpStatusCode.Forbidden)]
    [InlineData("/sprint?maxResults", HttpStatusCode.BadRequest)]
    public async Task A_pass_whose_board_or_sprint_listing_is_refused_archives_no_sprint_and_keeps_its_issues(string listing, HttpStatusCode status)
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Active.Id);
        var before = Assert.Single(await JiraKit.SprintsAsync(s.World, s.Project));
        Assert.Equal(before.Id, (await JiraKit.IssueAsync(s.World, s.Ready.Key)).SprintId);
        api.Jira.Fail(s.World.Site, listing, status);

        var run = await JiraKit.PassAsync(s.World);

        Assert.Equal("ok", run.GetProperty("status").GetString());
        var sprint = Assert.Single(await JiraKit.SprintsAsync(s.World, s.Project));
        Assert.Null(sprint.ArchivedAt);
        Assert.Equal(sprint.Id, (await JiraKit.IssueAsync(s.World, s.Ready.Key)).SprintId);
        Assert.Equal(sprint.Id, (await JiraKit.IssueAsync(s.World, s.Working.Key)).SprintId);

        // A whole listing still archives a sprint that is really gone.
        s.Active.Deleted = true;
        await JiraKit.PassAsync(s.World);

        Assert.NotNull(Assert.Single(await JiraKit.SprintsAsync(s.World, s.Project)).ArchivedAt);
    }
}
