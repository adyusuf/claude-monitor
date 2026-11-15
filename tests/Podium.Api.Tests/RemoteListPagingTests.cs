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
}
