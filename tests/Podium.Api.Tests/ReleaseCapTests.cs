using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The release cap counts the releases still to come: one that went out is history, cannot be deleted, and so must not fill the calendar for good (F-259).</summary>
[Collection(ApiGroup.Name)]
public sealed class ReleaseCapTests(ApiFactory api)
{
    private const int ReleasesMax = 200; // the contract of ADR-0011, written out so that changing it fails a test on purpose

    private async Task SeedAsync(Guid workspace, Guid app, int count, string status, string prefix, DateOnly first)
    {
        await using var db = api.Db();
        for (var i = 0; i < count; i++)
        {
            var name = $"{prefix}{i}";
            db.ApplicationReleases.Add(new ApplicationRelease { WorkspaceId = workspace, ApplicationId = app, Name = name, NameSearch = name.ToLowerInvariant(), PlannedDate = first.AddDays(i), Status = status, CreatedAt = api.Clock.GetUtcNow() });
        }

        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> AddAsync(TestUser who, Guid app, string name, string? status = null) =>
        who.PostAsync($"/api/applications/{app}/releases", new { name, status });

    [Fact]
    public async Task The_cap_is_on_planned_releases_the_200th_is_taken_the_201st_is_refused_and_199_leave_room()
    {
        var t = await IssueKit.TeamAsync(api); // the team's application already has one planned release
        await SeedAsync(t.Owner.WorkspaceId, t.Application, ReleasesMax - 2, ReleaseStatuses.Planned, "p", new DateOnly(2027, 1, 1)); // 199 planned in all

        Assert.Equal(HttpStatusCode.Created, (await AddAsync(t.Owner, t.Application, "the 200th")).StatusCode);
        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(await AddAsync(t.Owner, t.Application, "the 201st")));
    }

    [Fact]
    public async Task Releases_that_went_out_do_not_count_so_a_full_history_never_blocks_the_next_release()
    {
        var t = await IssueKit.TeamAsync(api);
        await SeedAsync(t.Owner.WorkspaceId, t.Application, ReleasesMax + 5, ReleaseStatuses.Released, "out", new DateOnly(2020, 1, 1)); // more than the cap, all history

        var response = await AddAsync(t.Owner, t.Application, "next");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_full_calendar_has_room_again_when_a_planned_release_goes_out_and_is_full_again_at_the_cap()
    {
        var t = await IssueKit.TeamAsync(api);
        await SeedAsync(t.Owner.WorkspaceId, t.Application, ReleasesMax - 1, ReleaseStatuses.Planned, "p", new DateOnly(2027, 1, 1)); // 200 planned
        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(await AddAsync(t.Owner, t.Application, "refused")));

        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/releases/{t.Release}", new { name = "1.0.0", status = "released" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, (await AddAsync(t.Owner, t.Application, "room again")).StatusCode);
        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(await AddAsync(t.Owner, t.Application, "full again")));
    }

    [Fact]
    public async Task A_release_that_already_went_out_can_be_recorded_while_the_calendar_is_full()
    {
        var t = await IssueKit.TeamAsync(api);
        await SeedAsync(t.Owner.WorkspaceId, t.Application, ReleasesMax - 1, ReleaseStatuses.Planned, "p", new DateOnly(2027, 1, 1)); // 200 planned

        var response = await AddAsync(t.Owner, t.Application, "shipped long ago", ReleaseStatuses.Released);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_list_keeps_every_planned_release_when_the_history_is_longer_than_the_cap()
    {
        var t = await IssueKit.TeamAsync(api); // one planned release, 1.0.0, without a date
        await SeedAsync(t.Owner.WorkspaceId, t.Application, ReleasesMax + 5, ReleaseStatuses.Released, "out", new DateOnly(2020, 1, 1));
        await SeedAsync(t.Owner.WorkspaceId, t.Application, 3, ReleaseStatuses.Planned, "soon", new DateOnly(2027, 6, 1));

        var rows = (await t.Owner.GetJsonAsync($"/api/applications/{t.Application}/releases")).EnumerateArray().ToList();

        var planned = rows.Where(r => r.GetProperty("status").GetString() == ReleaseStatuses.Planned).Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(["soon0", "soon1", "soon2", "1.0.0"], planned);
        Assert.Equal(ReleasesMax, rows.Count(r => r.GetProperty("status").GetString() == ReleaseStatuses.Released));
    }
}
