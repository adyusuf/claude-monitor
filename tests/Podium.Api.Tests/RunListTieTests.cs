using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The run list pages by (created time, id): runs created at the very same moment are neither skipped nor repeated across a page boundary (F-167).</summary>
[Collection(ApiGroup.Name)]
public sealed class RunListTieTests(ApiFactory api)
{
    private static async Task<List<JsonElement>> ListAsync(TestUser user, Guid workspace, string query) =>
        (await user.GetJsonAsync($"/api/workspaces/{workspace}/runs{query}")).EnumerateArray().ToList();

    private static List<Guid> Ids(IEnumerable<JsonElement> rows) => rows.Select(r => r.GetProperty("id").GetGuid()).ToList();

    private async Task<RemoteTeam> TeamWithTiedRunsAsync(int count)
    {
        var team = await RemoteKit.TeamAsync(api);
        for (var i = 0; i < count; i++) await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp"));
        var at = api.Clock.GetUtcNow();
        await using var db = api.Db();
        await db.Database.ExecuteSqlAsync($"UPDATE remote_runs SET created_at = {at} WHERE workspace_id = {team.WorkspaceId}");
        return team;
    }

    [Fact]
    public async Task Before_with_the_last_runs_id_continues_inside_a_group_of_runs_with_the_same_created_time()
    {
        var team = await TeamWithTiedRunsAsync(5);
        var all = Ids(await ListAsync(team.Admin, team.WorkspaceId, ""));

        var pages = new List<Guid>();
        string query = "?limit=2";
        for (var page = 0; page < 5; page++)
        {
            var rows = await ListAsync(team.Admin, team.WorkspaceId, query);
            if (rows.Count == 0) break;
            pages.AddRange(Ids(rows));
            var last = rows[^1];
            query = $"?limit=2&before={Uri.EscapeDataString(last.GetProperty("createdAt").GetString()!)}&beforeId={last.GetProperty("id").GetGuid()}";
        }

        Assert.Equal(all, pages);
        Assert.Equal(5, pages.Distinct().Count());
    }

    [Fact]
    public async Task Before_alone_still_means_strictly_older_so_an_older_client_gets_what_it_got_before()
    {
        var team = await TeamWithTiedRunsAsync(3);
        var first = (await ListAsync(team.Admin, team.WorkspaceId, "?limit=1"))[0];

        var older = await ListAsync(team.Admin, team.WorkspaceId, $"?before={Uri.EscapeDataString(first.GetProperty("createdAt").GetString()!)}");

        Assert.Empty(older);
    }
}
