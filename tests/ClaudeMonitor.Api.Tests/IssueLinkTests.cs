using System.Net;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>"Blocks" and "blocked by" between issues (ADR-0011 §4).</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueLinkTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> Link(TestUser user, Guid issue, Guid other, string kind) =>
        user.PostAsync($"/api/issues/{issue}/links", new { otherIssueId = other, kind });

    private static IEnumerable<int> Numbers(System.Text.Json.JsonElement list) => list.EnumerateArray().Select(l => l.GetProperty("number").GetInt32());

    [Fact]
    public async Task One_link_shows_as_blocks_on_one_end_and_blocked_by_on_the_other()
    {
        var t = await IssueKit.TeamAsync(api);
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A");
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B");
        var c = await IssueKit.IssueAsync(t.Owner, t.Project, "C");

        Assert.Equal(HttpStatusCode.NoContent, (await Link(t.Contributor, a, b, "blocks")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Link(t.Contributor, a, c, "blocked_by")).StatusCode);

        var viewA = await IssueKit.ViewAsync(t.Reviewer, a);
        Assert.Equal([2], Numbers(viewA.GetProperty("blocks")));
        Assert.Equal([3], Numbers(viewA.GetProperty("blockedBy")));
        Assert.Equal([1], Numbers((await IssueKit.ViewAsync(t.Owner, b)).GetProperty("blockedBy")));
        Assert.Equal([1], Numbers((await IssueKit.ViewAsync(t.Owner, c)).GetProperty("blocks")));
        Assert.Equal("B", viewA.GetProperty("blocks")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_link_to_itself_a_duplicate_a_loop_of_any_length_and_a_foreign_issue_are_refused()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A");
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B");
        var c = await IssueKit.IssueAsync(t.Owner, t.Project, "C");
        var gone = await IssueKit.IssueAsync(t.Owner, t.Project, "Gone");
        var foreign = await IssueKit.IssueAsync(other.Owner, other.Project, "Foreign");
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{gone}")).EnsureSuccessStatusCode();
        (await Link(t.Owner, a, b, "blocks")).EnsureSuccessStatusCode();
        (await Link(t.Owner, b, c, "blocks")).EnsureSuccessStatusCode();

        Assert.Equal(("otherIssueId", "self_link"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, a, "blocks")));
        Assert.Equal(("otherIssueId", "duplicate_link"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, b, "blocks")));
        Assert.Equal(("otherIssueId", "duplicate_link"), await ProjectKit.ProblemAsync(await Link(t.Owner, b, a, "blocked_by")));
        Assert.Equal(("otherIssueId", "cycle"), await ProjectKit.ProblemAsync(await Link(t.Owner, b, a, "blocks")));
        Assert.Equal(("otherIssueId", "cycle"), await ProjectKit.ProblemAsync(await Link(t.Owner, c, a, "blocks")));
        Assert.Equal(("otherIssueId", "cycle"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, c, "blocked_by")));
        Assert.Equal(("otherIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, foreign, "blocks")));
        Assert.Equal(("otherIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, gone, "blocks")));
        Assert.Equal(("otherIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, Guid.NewGuid(), "blocks")));
        Assert.Equal(("kind", "invalid_value"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, c, "relates")));
        Assert.Equal(("otherIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{a}/links", new { kind = "blocks" })));
        Assert.Equal(HttpStatusCode.NoContent, (await Link(t.Owner, a, c, "blocks")).StatusCode); // a diamond is not a loop
    }

    [Fact]
    public async Task A_link_is_removed_from_either_end_by_a_worker_only()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("lk-stranger");
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A");
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B");
        (await Link(t.Owner, a, b, "blocks")).EnsureSuccessStatusCode();
        var linkId = (await IssueKit.ViewAsync(t.Owner, b)).GetProperty("blockedBy")[0].GetProperty("linkId").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Reviewer.SendAsync(HttpMethod.Delete, $"/api/issue-links/{linkId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Link(t.Viewer, a, b, "blocks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Delete, $"/api/issue-links/{linkId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/issues/{a}/links", new { otherIssueId = b, kind = "blocks" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Contributor.SendAsync(HttpMethod.Delete, $"/api/issue-links/{linkId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issue-links/{linkId}")).StatusCode);
        Assert.Empty((await IssueKit.ViewAsync(t.Owner, a)).GetProperty("blocks").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await Link(t.Owner, b, a, "blocks")).StatusCode); // the pair is free again, in the other direction too
    }

    [Fact]
    public async Task An_archived_issue_takes_no_new_link()
    {
        var t = await IssueKit.TeamAsync(api);
        var a = await IssueKit.IssueAsync(t.Owner, t.Project, "A");
        var b = await IssueKit.IssueAsync(t.Owner, t.Project, "B");
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{a}")).EnsureSuccessStatusCode();

        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await Link(t.Owner, a, b, "blocks")));
    }
}
