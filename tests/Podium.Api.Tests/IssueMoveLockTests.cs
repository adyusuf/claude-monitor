using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// The project lock of a move (F-114): the test holds the project's row, so a move that takes the lock waits on it, and the interleaving is forced
/// by the database, not by timing. Without the lock in the endpoint the moves would not wait and this test fails (see RowLockHold).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IssueMoveLockTests(ApiFactory api)
{
    [Fact]
    public async Task Two_moves_after_the_same_issue_wait_on_the_project_lock_and_end_with_four_different_positions()
    {
        var t = await IssueKit.TeamAsync(api);
        var ids = new List<Guid>();
        foreach (var title in new[] { "A", "B", "C", "D" }) ids.Add(await IssueKit.IssueAsync(t.Owner, t.Project, title));

        var both = await RowLockHold.RunAsync(api, $"SELECT 1 FROM projects WHERE id = {t.Project} FOR UPDATE",
            () => t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{ids[0]}/position", new { afterIssueId = ids[2] }),
            () => t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{ids[1]}/position", new { afterIssueId = ids[2] }));

        Assert.All(both, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        var rows = (await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/issues")).GetProperty("items").EnumerateArray().ToList();
        var positions = rows.Select(r => r.GetProperty("position").GetDouble()).ToList();
        Assert.Equal(4, positions.Distinct().Count()); // moved one after the other: the second saw the first's position, so no two share one
        var titles = rows.Select(r => r.GetProperty("title").GetString()).ToList();
        Assert.Equal("D", titles[^1]);
        Assert.Equal("C", titles[0]);
    }
}
