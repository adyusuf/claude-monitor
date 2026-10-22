using System.Net;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>A webhook delivery taken by the worker (ADR-0015 §6): the record is read again from Jira and applied, or archived, or ignored when it was never imported.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationSyncTests(ApiFactory api)
{
    private static async Task<JiraWorld> ImportedAsync(ApiFactory api, Action<FakeSpace> fill, string key = "ABC")
    {
        FakeSpace? space = null;
        var w = await JiraKit.ConnectedAsync(api, site => fill(space = site.Space(key)));
        await JiraKit.ImportAsync(w, key);
        return w;
    }

    private async Task DeliverAsync(JiraWorld w, string body) =>
        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, body, "d-" + Guid.NewGuid().ToString("N"))).StatusCode);

    private static Task<bool> TitleIs(JiraWorld w, string key, string title) => JiraKit.IssueAsync(w, key).ContinueWith(t => t.Result.Title == title, TaskScheduler.Default);

    [Fact]
    public async Task An_updated_issue_is_read_again_and_the_podium_issue_follows()
    {
        FakeIssue? first = null;
        var w = await ImportedAsync(api, s =>
        {
            first = s.Issue("Before");
            s.Issue("Untouched");
        });
        first!.Summary = "After";
        first.Status = "In Review";
        first.Category = "indeterminate";
        first.Priority = "Highest";
        first.Points = 8;
        first.Description = "New text";
        api.Jira.Touch(first);
        await using var worker = await RunningWorker.StartAsync(api);

        await DeliverAsync(w, WebhookKit.IssueEvent("ABC-1"));

        await JiraKit.UntilAsync(() => TitleIs(w, "ABC-1", "After"), "the issue to be read again");
        var issue = await JiraKit.IssueAsync(w, "ABC-1");
        Assert.Equal(("in_review", "1", "8", "New text"), (issue.Status, issue.Priority, issue.Estimate, issue.Description));
        Assert.Equal("Untouched", (await JiraKit.IssueAsync(w, "ABC-2")).Title);
        Assert.Equal(2, (await JiraKit.IssuesAsync(w, issue.ProjectId)).Count);
    }

    [Fact]
    public async Task An_issue_created_in_jira_is_added_with_the_next_number_and_a_new_epic_comes_with_it()
    {
        var w = await ImportedAsync(api, s => s.Issue("Existing"));
        var space = w.Site.Spaces.Single();
        space.Issue("Brand new epic", i => { i.Type = "Epic"; i.Category = "done"; i.Status = "Done"; });
        space.Issue("Brand new child", i => i.ParentKey = "ABC-2");
        await using var worker = await RunningWorker.StartAsync(api);

        await DeliverAsync(w, WebhookKit.IssueEvent("ABC-3", "jira:issue_created"));

        await JiraKit.UntilAsync(async () => (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "ABC"))).Count == 3, "the new issue");
        var issues = (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "ABC"))).ToDictionary(i => i.Title);
        Assert.Equal([1, 2, 3], issues.Values.Select(i => i.Number).Order());
        Assert.Equal(issues["Brand new epic"].Id, issues["Brand new child"].ParentId); // the epic was fetched with it
        Assert.True(issues["Brand new child"].Position > issues["Existing"].Position);
    }

    [Fact]
    public async Task An_issue_deleted_in_jira_is_archived_never_deleted()
    {
        FakeIssue? doomed = null;
        var w = await ImportedAsync(api, s =>
        {
            doomed = s.Issue("Doomed");
            s.Issue("Survivor");
        });
        doomed!.Deleted = true;
        await using var worker = await RunningWorker.StartAsync(api);

        await DeliverAsync(w, WebhookKit.IssueEvent("ABC-1", "jira:issue_deleted"));

        await JiraKit.UntilAsync(async () => (await JiraKit.IssueAsync(w, "ABC-1")).ArchivedAt is not null, "the issue to be archived");
        Assert.Null((await JiraKit.IssueAsync(w, "ABC-2")).ArchivedAt);
        Assert.Equal(2, (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "ABC"))).Count);
    }

    [Fact]
    public async Task An_event_for_something_that_was_never_imported_changes_nothing()
    {
        var w = await ImportedAsync(api, s => s.Issue("Imported"));
        var other = w.Site.Space("OTH");
        other.Issue("In a space that was not imported");
        var stray = w.Site.Sprint(w.Site.Spaces[0], "Never imported", "active", "2026-10-15", "2026-10-28");
        await using var worker = await RunningWorker.StartAsync(api);
        var requestsBefore = api.Jira.Count(w.Site, "/sprint/");

        await DeliverAsync(w, WebhookKit.IssueEvent("OTH-1"));
        await DeliverAsync(w, WebhookKit.SprintEvent(stray.IdText, "sprint_started"));
        w.Site.Spaces[0].Issues[0].Summary = "Imported, then renamed";
        api.Jira.Touch(w.Site.Spaces[0].Issues[0]);
        await DeliverAsync(w, WebhookKit.IssueEvent("ABC-1")); // the sentinel, queued last: when it is done the others were too

        await JiraKit.UntilAsync(() => TitleIs(w, "ABC-1", "Imported, then renamed"), "the sentinel");
        await using var db = api.Db();
        Assert.Equal(1, await db.IntegrationLinks.CountAsync(l => l.ConnectionId == w.ConnectionId && l.Kind == LinkKinds.Project));
        Assert.Equal(0, await db.IntegrationLinks.CountAsync(l => l.ConnectionId == w.ConnectionId && l.Kind == LinkKinds.Sprint));
        Assert.Equal(0, await db.Sprints.CountAsync(s => s.SourceConnectionId == w.ConnectionId));
        Assert.Equal(1, await db.IntegrationLinks.CountAsync(l => l.ConnectionId == w.ConnectionId && l.Kind == LinkKinds.Issue)); // nothing of OTH came in
        Assert.Equal(["Imported, then renamed"], await db.Issues.Where(i => i.ExternalKey == "ABC-1" && i.WorkspaceId == w.WorkspaceId).Select(i => i.Title).ToListAsync());
        Assert.Equal(requestsBefore, api.Jira.Count(w.Site, "/sprint/")); // Jira was not even asked about the stray sprint
    }

    [Fact]
    public async Task A_sprint_event_updates_an_imported_sprint_and_the_issues_in_it_and_a_deleted_one_is_archived()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Active.Id, s.Future.Id);
        s.Active.Name = "Sprint two, renamed";
        s.Active.Goal = "A new goal";
        s.Working.SprintId = s.Future.Id; // moved to the future sprint in Jira
        api.Jira.Touch(s.Working);
        s.Future.Deleted = true;
        await using var worker = await RunningWorker.StartAsync(api);

        await DeliverAsync(s.World, WebhookKit.SprintEvent(s.Active.IdText, "sprint_updated"));
        await DeliverAsync(s.World, WebhookKit.SprintEvent(s.Future.IdText, "sprint_deleted"));

        await JiraKit.UntilAsync(async () => (await JiraKit.SprintsAsync(s.World, s.Project)).Any(x => x.Name == "Sprint two, renamed")
                                              && (await JiraKit.SprintsAsync(s.World, s.Project)).Single(x => x.Name == "Sprint three").ArchivedAt is not null, "both sprint events");
        var sprints = (await JiraKit.SprintsAsync(s.World, s.Project)).ToDictionary(x => x.Name!);
        Assert.Equal(("A new goal", "active", null), (sprints["Sprint two, renamed"].Goal, sprints["Sprint two, renamed"].Status, sprints["Sprint two, renamed"].ArchivedAt));
        Assert.NotNull(sprints["Sprint three"].ArchivedAt);
        var planned = await JiraKit.IssueAsync(s.World, "SPR-6");
        Assert.Null(planned.SprintId); // the issue of the deleted sprint is back in the backlog
    }

    [Fact]
    public async Task The_worker_goes_on_after_a_request_it_cannot_serve()
    {
        var gone = await JiraKit.ConnectedAsync(api);
        (await gone.Admin.SendAsync(HttpMethod.Delete, gone.Base)).EnsureSuccessStatusCode();
        var w = await ImportedAsync(api, s =>
        {
            s.Issue("Fails");
            s.Issue("Works");
        });
        api.Jira.Fail(w.Site, "/issue/ABC-1", HttpStatusCode.InternalServerError, times: 5);
        w.Site.Spaces[0].Issues[1].Summary = "Works, renamed";
        api.Jira.Touch(w.Site.Spaces[0].Issues[1]);
        var inbox = api.Services.GetRequiredService<SyncInbox>();
        await using var worker = await RunningWorker.StartAsync(api);

        inbox.Enqueue(new SyncRequest(gone.ConnectionId, SyncTarget.Issue, "ABC-1")); // a connection that is no longer there
        inbox.Enqueue(new SyncRequest(w.ConnectionId, SyncTarget.Issue, "ABC-1")); // Jira answers 500
        inbox.Enqueue(new SyncRequest(w.ConnectionId, SyncTarget.Issue, "ABC-2"));

        await JiraKit.UntilAsync(() => TitleIs(w, "ABC-2", "Works, renamed"), "the request after the failing ones");
        Assert.Equal("Fails", (await JiraKit.IssueAsync(w, "ABC-1")).Title);
    }

    [Fact]
    public async Task A_read_for_a_connection_that_must_sign_in_again_asks_jira_nothing()
    {
        var w = await ImportedAsync(api, s => s.Issue("One"));
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == w.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Reauthorize));
        }

        var before = api.Jira.Count(w.Site, "/issue/ABC-1");
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncService>().SyncIssueAsync(await JiraKit.ConnectionAsync(w), "ABC-1", CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<SyncService>().SyncSprintAsync(await JiraKit.ConnectionAsync(w), "1", CancellationToken.None);

        Assert.Equal(before, api.Jira.Count(w.Site, "/issue/ABC-1"));
    }
}
