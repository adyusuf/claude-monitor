using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-066: a reconcile pass that does not see a mirrored space or issue only counts the miss; the second pass in a row that misses it archives,
/// and a pass that sees it again starts the count over (a connector who loses a permission for a moment archives nothing).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPassMissTests(ApiFactory api)
{
    private static int Archived(System.Text.Json.JsonElement run) => run.GetProperty("archived").GetInt32();

    /// <summary>The project's status and the missed passes counted on its space link.</summary>
    private async Task<(string Status, int Missed)> SpaceAsync(SprintWorld s)
    {
        await using var db = api.Db();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Project);
        var link = await db.IntegrationLinks.AsNoTracking().SingleAsync(l => l.ConnectionId == s.World.ConnectionId && l.Kind == LinkKinds.Project && l.LocalId == s.Project);
        return (project.Status, link.MissedPasses);
    }

    [Fact]
    public async Task A_space_the_connector_loses_for_one_pass_archives_nothing_and_seeing_it_again_starts_the_count_over()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        s.Space.Deleted = true; // the project answers NotFound to the connector, as on a lost browse permission

        var lost = await JiraKit.PassAsync(s.World);

        Assert.Equal((0, "ok"), (Archived(lost), lost.GetProperty("status").GetString()));
        Assert.Equal((ManagedStatuses.Active, 1), await SpaceAsync(s));
        Assert.All(await JiraKit.IssuesAsync(s.World, s.Project), i => Assert.Null(i.ArchivedAt));
        s.Space.Deleted = false;

        await JiraKit.PassAsync(s.World);

        Assert.Equal((ManagedStatuses.Active, 0), await SpaceAsync(s));
        s.Space.Deleted = true;

        var again = await JiraKit.PassAsync(s.World); // one miss after the reset is again only one

        Assert.Equal(0, Archived(again));
        Assert.Equal((ManagedStatuses.Active, 1), await SpaceAsync(s));
    }

    [Fact]
    public async Task A_space_missing_on_two_passes_in_a_row_archives_its_project_and_keeps_its_issues()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        s.Space.Deleted = true;

        var first = await JiraKit.PassAsync(s.World);
        var second = await JiraKit.PassAsync(s.World);

        Assert.Equal((0, 1), (Archived(first), Archived(second)));
        Assert.Equal(ManagedStatuses.Archived, (await SpaceAsync(s)).Status);
        Assert.All(await JiraKit.IssuesAsync(s.World, s.Project), i => Assert.Null(i.ArchivedAt)); // the project is archived, its rows stay
    }

    [Fact]
    public async Task An_issue_missing_from_one_whole_listing_stays_and_one_that_comes_back_starts_the_count_over()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        s.Loose.Deleted = true;

        var lost = await JiraKit.PassAsync(s.World);

        Assert.Equal(0, Archived(lost));
        Assert.Null((await JiraKit.IssueAsync(s.World, s.Loose.Key)).ArchivedAt);
        s.Loose.Deleted = false;

        await JiraKit.PassAsync(s.World);
        s.Loose.Deleted = true;
        var again = await JiraKit.PassAsync(s.World);

        Assert.Equal(0, Archived(again)); // the pass between saw it: this is the first miss again
        Assert.Null((await JiraKit.IssueAsync(s.World, s.Loose.Key)).ArchivedAt);

        var second = await JiraKit.PassAsync(s.World);

        Assert.Equal(1, Archived(second));
        Assert.NotNull((await JiraKit.IssueAsync(s.World, s.Loose.Key)).ArchivedAt);
        Assert.Null((await JiraKit.IssueAsync(s.World, s.Ready.Key)).ArchivedAt);
    }
}
