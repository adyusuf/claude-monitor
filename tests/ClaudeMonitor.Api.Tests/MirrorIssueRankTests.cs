using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>A move in the backlog is a rank change in Jira first (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueRankTests(ApiFactory api)
{
    private const string Rank = "/rest/agile/1.0/issue/rank";

    private static Task<HttpResponseMessage> Move(TestUser who, Guid issue, Guid? after) => who.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/position", new { afterIssueId = after });

    private static async Task<List<string?>> OrderAsync(WriteWorld w) => [.. (await JiraKit.IssuesAsync(w.World, w.Project)).OrderBy(i => i.Position).Select(i => i.ExternalKey)];

    [Fact]
    public async Task Putting_an_issue_after_another_ranks_it_after_it_in_jira_and_places_it_there_here()
    {
        var w = await JiraWriteKit.OpenAsync(api);

        Assert.Equal(HttpStatusCode.NoContent, (await Move(w.Member, await w.IdAsync("WRT-4"), await w.IdAsync("WRT-2"))).StatusCode);

        var body = w.Jira.Sent(w.Site, HttpMethod.Put, Rank);
        Assert.Equal(("WRT-4", "WRT-2", false), (body["issues"]![0]!.GetValue<string>(), body["rankAfterIssue"]!.GetValue<string>(), body.ContainsKey("rankBeforeIssue")));
        Assert.Equal([w.MemberAccount!.Id], w.Jira.Callers(w.Site, HttpMethod.Put, Rank));
        Assert.Equal(["WRT-1", "WRT-2", "WRT-4", "WRT-3"], w.Space.Issues.Select(i => i.Key));
        Assert.Equal(["WRT-1", "WRT-2", "WRT-4", "WRT-3"], await OrderAsync(w));
    }

    [Fact]
    public async Task Putting_an_issue_at_the_top_ranks_it_before_the_first_other_issue_of_the_backlog()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var first = (await OrderAsync(w))[0]!;

        Assert.Equal(HttpStatusCode.NoContent, (await Move(w.Member, await w.IdAsync("WRT-3"), null)).StatusCode);

        var body = w.Jira.Sent(w.Site, HttpMethod.Put, Rank);
        Assert.Equal(("WRT-3", first, false), (body["issues"]![0]!.GetValue<string>(), body["rankBeforeIssue"]!.GetValue<string>(), body.ContainsKey("rankAfterIssue")));
        Assert.Equal(["WRT-3", "WRT-1", "WRT-2", "WRT-4"], w.Space.Issues.Select(i => i.Key));
        Assert.Equal(["WRT-3", "WRT-1", "WRT-2", "WRT-4"], await OrderAsync(w));
    }

    [Fact]
    public async Task An_issue_already_first_is_ranked_before_the_next_one_and_not_before_itself()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var order = await OrderAsync(w);

        Assert.Equal(HttpStatusCode.NoContent, (await Move(w.Member, await w.IdAsync(order[0]!), null)).StatusCode);

        Assert.Equal(order[1], w.Jira.Sent(w.Site, HttpMethod.Put, Rank)["rankBeforeIssue"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_anchor_of_another_project_is_invalid_and_one_that_is_not_in_jira_is_out_of_scope_and_neither_calls_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var other = (await ProjectKit.CreateProjectAsync(w.World.Admin, "Elsewhere")).GetProperty("id").GetGuid();
        var foreign = await IssueKit.IssueAsync(w.World.Admin, other, "Foreign");
        var now = DateTimeOffset.UtcNow;
        var local = new Issue
        {
            WorkspaceId = w.World.WorkspaceId,
            ProjectId = w.Project,
            Number = 99,
            Title = "Local only",
            TitleSearch = "local only",
            Position = 99999,
            CreatedBy = w.World.Admin.Id,
            UpdatedBy = w.World.Admin.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using (var db = api.Db())
        {
            db.Issues.Add(local);
            await db.SaveChangesAsync();
        }

        var invalid = await Move(w.Member, await w.IdAsync("WRT-2"), foreign);
        var scope = await Move(w.Member, await w.IdAsync("WRT-2"), local.Id);

        Assert.Equal(("afterIssueId", "invalid_issue"), await ProjectKit.ProblemAsync(invalid));
        await JiraWriteKit.AssertRefusedAsync(scope, HttpStatusCode.Conflict, "mirrored_scope");
        Assert.Empty(w.Jira.Writes(w.Site));
    }

    [Fact]
    public async Task Without_an_account_or_against_jiras_refusal_the_issue_stays_where_it_was()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");
        (await w.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{w.Project}/members/{lonely.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        var order = await OrderAsync(w);
        var id = await w.IdAsync("WRT-4");
        var after = await w.IdAsync("WRT-1");

        await JiraWriteKit.AssertRefusedAsync(await Move(lonely, id, after), HttpStatusCode.Conflict, "jira_account_required");
        w.Site.Refuse(HttpMethod.Put, Rank, HttpStatusCode.Forbidden);
        await JiraWriteKit.AssertRefusedAsync(await Move(w.Member, id, after), HttpStatusCode.Forbidden, "tracker_forbidden");

        Assert.Equal(order, await OrderAsync(w));
        Assert.Equal(["WRT-1", "WRT-2", "WRT-3", "WRT-4"], w.Space.Issues.Select(i => i.Key));
    }
}
