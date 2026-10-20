using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The reconciliation pass (ADR-0015 §6): the net under the webhooks. What it applies, what it archives, and what it leaves alone.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPassTests(ApiFactory api)
{
    private static int Count(System.Text.Json.JsonElement run, string name) => run.GetProperty(name).GetInt32();

    [Fact]
    public async Task A_pass_records_a_run_sets_the_time_of_the_last_pass_and_clears_an_earlier_error()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == s.World.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.LastError, "tracker_unavailable"));
        }

        var run = await JiraKit.PassAsync(s.World);

        Assert.Equal(("pass", "ok", 3, 0), (run.GetProperty("kind").GetString(), run.GetProperty("status").GetString(), Count(run, "created"), Count(run, "archived"))); // the issues the backlog did not hold
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, run.GetProperty("endedAt").ValueKind);
        var connection = await JiraKit.ConnectionAsync(s.World);
        Assert.Equal((api.Clock.GetUtcNow(), null), (connection.LastPassAt, connection.LastError));
        var shown = (await s.World.Admin.GetJsonAsync($"{s.World.Base}/runs")).EnumerateArray().ToList();
        Assert.Contains(shown, r => r.GetProperty("kind").GetString() == "pass" && r.GetProperty("id").GetGuid() == run.GetProperty("id").GetGuid());
        Assert.True(shown.Count <= 10);
        await JiraKit.AssertNoSecretAnywhereAsync(s.World);
        // Tokens go to Atlassian's two hosts only, and the bearer token only to the API host.
        Assert.All(api.Jira.Requests, r => Assert.Contains(r.Host, new[] { FakeJira.AuthHost, FakeJira.ApiHost }));
        Assert.All(api.Jira.Requests.Where(r => r.Bearer is not null), r => Assert.Equal(FakeJira.ApiHost, r.Host));
    }

    [Fact]
    public async Task A_changed_issue_is_updated_and_a_sprint_that_changed_follows()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Active.Id);
        s.Ready.Summary = "Renamed in Jira";
        s.Ready.Priority = "Lowest";
        api.Jira.Touch(s.Ready);
        s.Active.Name = "Sprint two, renamed";
        s.Active.State = "closed";

        var run = await JiraKit.PassAsync(s.World);

        var issue = await JiraKit.IssueAsync(s.World, "SPR-4");
        Assert.Equal(("Renamed in Jira", "5"), (issue.Title, issue.Priority));
        var sprint = Assert.Single(await JiraKit.SprintsAsync(s.World, s.Project));
        Assert.Equal(("Sprint two, renamed", "closed"), (sprint.Name, sprint.Status));
        Assert.True(Count(run, "updated") >= 2);
    }

    [Fact]
    public async Task An_issue_deleted_in_jira_is_archived_once_and_a_space_that_vanished_archives_its_project()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        s.Loose.Deleted = true;

        var first = await JiraKit.PassAsync(s.World);
        var second = await JiraKit.PassAsync(s.World);

        var loose = await JiraKit.IssueAsync(s.World, "SPR-7");
        Assert.NotNull(loose.ArchivedAt);
        Assert.Null((await JiraKit.IssueAsync(s.World, "SPR-1")).ArchivedAt);
        Assert.Equal((1, 0), (Count(first, "archived"), Count(second, "archived")));
        s.Space.Deleted = true;

        var gone = await JiraKit.PassAsync(s.World);

        await using var db = api.Db();
        Assert.Equal(ManagedStatuses.Archived, (await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Project)).Status);
        Assert.Equal((1, "ok"), (Count(gone, "archived"), gone.GetProperty("status").GetString()));
        Assert.Equal(7, await db.Issues.CountAsync(i => i.ProjectId == s.Project)); // nothing is deleted
        Assert.Equal(0, Count(await JiraKit.PassAsync(s.World), "archived")); // and an archived project is left alone
    }

    [Fact]
    public async Task A_sprint_that_disappeared_is_archived_and_its_issues_go_back_to_the_backlog_and_a_new_sprint_is_not_imported()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Active.Id, s.Future.Id);
        s.Future.Deleted = true;
        s.Planned.SprintId = null; // as Jira does when a sprint is deleted: its issues return to the backlog
        api.Jira.Touch(s.Planned);
        var brandNew = s.World.Site.Sprint(s.Space, "Brand new", "future");
        s.Space.Issue("In the new sprint", i => i.SprintId = brandNew.Id);

        await JiraKit.PassAsync(s.World);

        var sprints = (await JiraKit.SprintsAsync(s.World, s.Project)).ToDictionary(x => x.Name!);
        Assert.Equal(["Sprint three", "Sprint two"], sprints.Keys.Order()); // the new one did not come in
        Assert.NotNull(sprints["Sprint three"].ArchivedAt);
        Assert.Null(sprints["Sprint two"].ArchivedAt);
        var planned = await JiraKit.IssueAsync(s.World, "SPR-6");
        Assert.Null(planned.SprintId);
        Assert.Null(planned.ArchivedAt);
        var inNew = await JiraKit.IssueAsync(s.World, "SPR-8"); // the issue itself is read; it just belongs to no sprint of Podium
        Assert.Equal((null, "backlog"), (inNew.SprintId, inNew.Status));
        await using var db = api.Db();
        Assert.Equal(2, await db.IntegrationLinks.CountAsync(l => l.ConnectionId == s.World.ConnectionId && l.Kind == LinkKinds.Sprint));
    }

    [Fact]
    public async Task A_pass_reads_only_what_changed_since_the_last_one_with_a_few_minutes_of_overlap()
    {
        FakeIssue? silent = null;
        FakeIssue? loud = null;
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var space = site.Space("INC");
            silent = space.Issue("Silent");
            loud = space.Issue("Loud");
        });
        await JiraKit.ImportAsync(w, "INC");
        api.Clock.Advance(TimeSpan.FromHours(2));
        await JiraKit.PassAsync(w); // the first pass reads from the connection's start
        var lastPass = (await JiraKit.ConnectionAsync(w)).LastPassAt!.Value;
        silent!.Summary = "Silent, edited without a new stamp"; // Jira's search would not list it: its stamp is old
        loud!.Summary = "Loud, edited";
        api.Jira.Touch(loud);

        await JiraKit.PassAsync(w);

        Assert.Equal("Silent", (await JiraKit.IssueAsync(w, "INC-1")).Title);
        Assert.Equal("Loud, edited", (await JiraKit.IssueAsync(w, "INC-2")).Title);
        var searches = api.Jira.Requests.Where(r => r.Path.StartsWith($"/ex/jira/{w.Site.CloudId}", StringComparison.Ordinal) && r.Path.EndsWith("/search/jql", StringComparison.Ordinal))
            .Select(r => System.Text.Json.JsonDocument.Parse(r.Body).RootElement.GetProperty("jql").GetString()!).Where(q => q.Contains("updated >=", StringComparison.Ordinal)).ToList();
        Assert.Contains($"updated >= \"{(lastPass - api.Services.GetRequiredService<ClaudeMonitor.Api.Config.ApiConfig>().IntegrationPassOverlap):yyyy-MM-dd HH:mm}\"", searches[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_of_a_connector_who_must_sign_in_again_fails_with_unauthorized_and_marks_the_connection()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await using (var db = api.Db())
        {
            var past = api.Clock.GetUtcNow().AddMinutes(-1);
            await db.IntegrationAccounts.Where(a => a.ConnectionId == s.World.ConnectionId).ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessExpiresAt, past));
        }

        s.World.Account.Revoked = true;

        var first = await JiraKit.PassAsync(s.World);
        var second = await JiraKit.PassAsync(s.World);

        Assert.Equal(("failed", "tracker_unauthorized"), (first.GetProperty("status").GetString(), first.GetProperty("error").GetString()));
        Assert.Equal(("failed", "tracker_unauthorized"), (second.GetProperty("status").GetString(), second.GetProperty("error").GetString()));
        var connection = await JiraKit.ConnectionAsync(s.World);
        Assert.Equal((ConnectionStatuses.Reauthorize, "tracker_unauthorized", null), (connection.Status, connection.LastError, connection.LastPassAt));
    }

    [Fact]
    public async Task A_pass_while_jira_is_down_fails_without_moving_the_time_of_the_last_pass_and_the_next_one_recovers()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await JiraKit.PassAsync(s.World);
        var before = (await JiraKit.ConnectionAsync(s.World)).LastPassAt;
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        api.Jira.Fail(s.World.Site, "/rest/api/3/project/SPR", HttpStatusCode.ServiceUnavailable);

        var down = await JiraKit.PassAsync(s.World);

        Assert.Equal(("failed", "tracker_unavailable"), (down.GetProperty("status").GetString(), down.GetProperty("error").GetString()));
        var connection = await JiraKit.ConnectionAsync(s.World);
        Assert.Equal((before, "tracker_unavailable", ConnectionStatuses.Active), (connection.LastPassAt, connection.LastError, connection.Status));
        Assert.DoesNotContain("secret detail", down.ToString(), StringComparison.Ordinal);
        Assert.Equal("ok", (await JiraKit.PassAsync(s.World)).GetProperty("status").GetString());
        Assert.Null((await JiraKit.ConnectionAsync(s.World)).LastError);
    }

    [Fact]
    public async Task Only_an_admin_may_ask_for_a_pass_and_any_member_may_read_the_runs()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var member = await RemoteKit.MemberAsync(api, s.World.Admin, "pass-member");
        var stranger = await api.NewClient().SignedUpAsync("pass-stranger");

        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsync($"{s.World.Base}/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"{s.World.Base}/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.Http.GetAsync($"{s.World.Base}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"{s.World.Base}/runs")).StatusCode);
        await using var db = api.Db();
        Assert.Equal(0, await db.IntegrationSyncRuns.CountAsync(r => r.ConnectionId == s.World.ConnectionId && r.Kind == SyncKinds.Pass));
    }
}
