using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>A machine's grants and jobs are never deleted, so the web reads them newest first a page at a time (limit, then before=the oldest shown).</summary>
[Collection(ApiGroup.Name)]
public sealed class RemoteListPagingTests(ApiFactory api)
{
    private static async Task<List<JsonElement>> PageAsync(TestUser reader, string path) =>
        (await reader.GetJsonAsync(path)).EnumerateArray().ToList();

    private static string Before(JsonElement row) => Uri.EscapeDataString(row.GetProperty("createdAt").GetString()!);

    private static List<Guid> Ids(IEnumerable<JsonElement> rows) => rows.Select(r => r.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Grants_come_newest_first_in_pages_of_the_limit_and_before_reads_the_older_ones()
    {
        var team = await RemoteKit.TeamAsync(api);
        var made = new List<Guid>();
        foreach (var max in new[] { 10, 20, 30 })
        {
            made.Add((await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId, max))).Id);
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await PageAsync(team.Owner, $"/api/agents/{team.TargetId}/grants?limit=2");
        var older = await PageAsync(team.Owner, $"/api/agents/{team.TargetId}/grants?limit=2&before={Before(first[^1])}");

        Assert.Equal([made[2], made[1]], Ids(first));
        Assert.Equal([made[0]], Ids(older));
    }

    [Fact]
    public async Task Jobs_come_newest_first_in_pages_of_the_limit_and_before_reads_the_older_ones()
    {
        var team = await RemoteKit.TeamAsync(api);
        var made = new List<Guid>();
        foreach (var name in new[] { "Run lint", "Run tests", "Run build" })
        {
            var response = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs",
                new JobProposal(team.TargetId, name, ["/opt/ci/run-tests", "--suite", "unit"], "/opt/ci", 300, "needs a run"), TestUser.Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            made.Add((await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!.Id);
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await PageAsync(team.Owner, $"/api/agents/{team.TargetId}/jobs?limit=2");
        var older = await PageAsync(team.Owner, $"/api/agents/{team.TargetId}/jobs?limit=2&before={Before(first[^1])}");

        Assert.Equal([made[2], made[1]], Ids(first));
        Assert.Equal([made[0]], Ids(older));
    }

    private async Task<(RemoteTeam Team, Guid Active, Guid Denied, Guid Proposed, Guid Retired)> JobsInEveryStatusAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        var ids = new List<Guid>();
        foreach (var name in new[] { "Run lint", "Run tests", "Run build", "Run docs" })
        {
            var response = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs",
                new JobProposal(team.TargetId, name, ["/opt/ci/run-tests", "--suite", "unit"], "/opt/ci", 300, "needs a run"), TestUser.Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!.Id);
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{ids[0]}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{ids[1]}/deny")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{ids[3]}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{ids[3]}/retire")).StatusCode);
        return (team, ids[0], ids[1], ids[2], ids[3]);
    }

    [Fact]
    public async Task Jobs_are_filtered_by_one_status_or_by_the_open_and_finished_groups_and_no_filter_still_returns_all()
    {
        var (team, active, denied, proposed, retired) = await JobsInEveryStatusAsync();
        var jobs = $"/api/agents/{team.TargetId}/jobs";

        Assert.Equal([retired, proposed, denied, active], Ids(await PageAsync(team.Owner, jobs)));
        Assert.Equal([active], Ids(await PageAsync(team.Owner, $"{jobs}?status=active")));
        Assert.Equal([denied], Ids(await PageAsync(team.Owner, $"{jobs}?status=denied")));
        Assert.Equal([proposed, active], Ids(await PageAsync(team.Owner, $"{jobs}?status=open")));
        Assert.Equal([retired, denied], Ids(await PageAsync(team.Owner, $"{jobs}?status=finished")));
        Assert.Equal([proposed, denied], Ids(await PageAsync(team.Owner, $"{jobs}?status=proposed,denied")));
    }

    [Fact]
    public async Task A_job_status_filter_pages_with_before_and_limit_and_an_unknown_status_is_refused()
    {
        var (team, active, _, proposed, _) = await JobsInEveryStatusAsync();
        var jobs = $"/api/agents/{team.TargetId}/jobs";

        var first = await PageAsync(team.Owner, $"{jobs}?status=open&limit=1");
        var older = await PageAsync(team.Owner, $"{jobs}?status=open&limit=1&before={Before(first[^1])}");

        Assert.Equal([proposed], Ids(first));
        Assert.Equal([active], Ids(older));
        var refused = await team.Owner.Http.GetAsync($"{jobs}?status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
