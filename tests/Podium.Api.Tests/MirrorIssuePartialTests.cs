using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A status change is a transition in Jira and the other fields a second call: when the second one fails the transition is already done, so Podium follows it and says it was applied only in part.</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssuePartialTests(ApiFactory api)
{
    private const string Issue = "/rest/api/3/issue/WRT-2";

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_failed_field_update_after_the_transition_keeps_the_new_status_here_and_says_it_was_partly_applied(HttpStatusCode second)
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        w.Site.Refuse(HttpMethod.Put, Issue, second, times: 3);

        var response = await JiraWriteKit.EditAsync(w.Member, id, new { status = "in_progress", title = "Started" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_partly_applied");
        Assert.Equal("In Progress", w.Site.Find("WRT-2")!.Status);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Story one", "in_progress"), (row.Title, row.Status)); // the status Jira took, not the title it did not
    }

    [Fact]
    public async Task The_retry_after_a_partial_write_is_not_refused_as_changed_in_jira_and_sends_only_the_fields()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        w.Site.Refuse(HttpMethod.Put, Issue, HttpStatusCode.Forbidden);
        await JiraWriteKit.EditAsync(w.Member, id, new { status = "in_progress", title = "Started" });
        var posts = w.Jira.Writes(w.Site).Count(c => c.StartsWith("POST", StringComparison.Ordinal));

        var retry = await JiraWriteKit.EditAsync(w.Member, id, new { status = "in_progress", title = "Started" });

        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(posts, w.Jira.Writes(w.Site).Count(c => c.StartsWith("POST", StringComparison.Ordinal))); // no second transition
        Assert.Equal(("Started", "in_progress"), ((await w.RowAsync("WRT-2")).Title, (await w.RowAsync("WRT-2")).Status));
    }

    [Fact]
    public async Task A_failure_of_the_transition_itself_changes_nothing_here_and_keeps_its_own_answer()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        var before = await w.RowAsync("WRT-2");
        w.Site.Refuse(HttpMethod.Post, Issue + "/transitions", HttpStatusCode.Forbidden);

        var response = await JiraWriteKit.EditAsync(w.Member, id, new { status = "in_progress", title = "Started" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Forbidden, "tracker_forbidden");
        var row = await w.RowAsync("WRT-2");
        Assert.Equal((before.Title, before.Status), (row.Title, row.Status));
    }
}
