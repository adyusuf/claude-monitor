using System.Net;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Blocking links added at the same moment (a double click, two people): the project lock decides, so no loop, duplicate or over-cap link appears.</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueLinkConcurrencyTests(ApiFactory api)
{
    private const int Rounds = 8;

    private static Task<HttpResponseMessage> Link(TestUser user, Guid issue, Guid other, string kind) =>
        user.PostAsync($"/api/issues/{issue}/links", new { otherIssueId = other, kind });

    [Fact]
    public async Task Two_opposite_blocks_added_at_once_never_leave_a_loop()
    {
        var t = await IssueKit.TeamAsync(api);
        var ids = await Task.WhenAll(Enumerable.Range(0, Rounds * 2).Select(n => IssueKit.IssueAsync(t.Owner, t.Project, $"Loop {n}")));

        for (var n = 0; n < Rounds; n++)
        {
            var (a, b) = (ids[n * 2], ids[(n * 2) + 1]);
            var both = await Task.WhenAll(Link(t.Owner, a, b, "blocks"), Link(t.Owner, b, a, "blocks"));
            Assert.Single(both, r => r.StatusCode == HttpStatusCode.NoContent);
            var refused = Assert.Single(both, r => r.StatusCode != HttpStatusCode.NoContent);
            Assert.Equal(("otherIssueId", "cycle"), await ProjectKit.ProblemAsync(refused));
        }
    }
    [Fact]
    public async Task The_same_link_added_twice_at_once_is_one_link_and_one_duplicate()
    {
        var t = await IssueKit.TeamAsync(api);
        var ids = await Task.WhenAll(Enumerable.Range(0, Rounds * 2).Select(n => IssueKit.IssueAsync(t.Owner, t.Project, $"Twice {n}")));

        for (var n = 0; n < Rounds; n++)
        {
            var both = await Task.WhenAll(Link(t.Owner, ids[n * 2], ids[(n * 2) + 1], "blocks"), Link(t.Owner, ids[n * 2], ids[(n * 2) + 1], "blocks"));
            Assert.Single(both, r => r.StatusCode == HttpStatusCode.NoContent);
            Assert.Equal(("otherIssueId", "duplicate_link"), await ProjectKit.ProblemAsync(Assert.Single(both, r => r.StatusCode != HttpStatusCode.NoContent)));
        }
    }
    [Fact]
    public async Task The_link_cap_counts_both_ends_of_a_link()
    {
        var t = await IssueKit.TeamAsync(api);
        var hub = await IssueKit.IssueAsync(t.Owner, t.Project, "Hub");
        var others = await Task.WhenAll(Enumerable.Range(0, IssueLinkEndpoints.PerIssueMax + 1).Select(n => IssueKit.IssueAsync(t.Owner, t.Project, $"Spoke {n}")));
        foreach (var other in others.Take(IssueLinkEndpoints.PerIssueMax)) (await Link(t.Owner, hub, other, "blocks")).EnsureSuccessStatusCode();

        Assert.Equal(("otherIssueId", "limit_reached"), await ProjectKit.ProblemAsync(await Link(t.Owner, hub, others[^1], "blocks")));
        Assert.Equal(("otherIssueId", "limit_reached"), await ProjectKit.ProblemAsync(await Link(t.Owner, others[^1], hub, "blocks")));
    }
}
