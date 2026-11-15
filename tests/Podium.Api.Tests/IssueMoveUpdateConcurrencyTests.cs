using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Two moves or two updates of the same issues at the same moment both succeed and leave a consistent backlog.</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueMoveUpdateConcurrencyTests(ApiFactory api)
{
    [Fact]
    public async Task Two_moves_at_once_both_succeed_and_keep_one_strict_order()
    {
        var t = await IssueKit.TeamAsync(api);
        var ids = new List<Guid>();
        foreach (var title in new[] { "A", "B", "C", "D" }) ids.Add(await IssueKit.IssueAsync(t.Owner, t.Project, title));

        var both = await Task.WhenAll(
            t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{ids[0]}/position", new { afterIssueId = ids[2] }),
            t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{ids[1]}/position", new { afterIssueId = ids[2] }));

        Assert.All(both, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        var titles = await IssueKit.TitlesAsync(t.Owner, $"/api/projects/{t.Project}/issues");
        Assert.Equal(4, titles.Count);
        Assert.Equal(["C", "D"], titles.Where(x => x is "C" or "D"));
        Assert.Equal(4, titles.Distinct().Count());
        Assert.Equal(titles.IndexOf("C") + 1, Math.Min(titles.IndexOf("A"), titles.IndexOf("B")));
    }
    [Fact]
    public async Task Two_updates_at_once_both_succeed_and_leave_one_of_the_two_values()
    {
        var t = await IssueKit.TeamAsync(api);
        var id = await IssueKit.IssueAsync(t.Owner, t.Project, "Original");

        var both = await Task.WhenAll(
            t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "First", priority = "must" }),
            t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "Second", priority = "could" }));

        Assert.All(both, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        var issue = (await IssueKit.ViewAsync(t.Owner, id)).GetProperty("issue");
        Assert.Contains($"{issue.GetProperty("title").GetString()}/{issue.GetProperty("priority").GetString()}", new[] { "First/must", "Second/could" });
    }
}
