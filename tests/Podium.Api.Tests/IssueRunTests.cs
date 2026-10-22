using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Remote runs attached to issues (ADR-0011 §6): who may link, what the link shows, and that nothing of the run's command or output leaks.</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueRunTests(ApiFactory api)
{
    private async Task<(RemoteTeam Team, Guid Project, Guid Issue, Guid Run)> SetUpAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        var project = (await ProjectKit.CreateProjectAsync(team.Admin, "Runs")).GetProperty("id").GetGuid();
        var issue = await IssueKit.IssueAsync(team.Admin, project, "Failing build", "bug");
        var run = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), [RemoteKit.Tail, "/var/log/hush-command.log"]))).Id;
        return (team, project, issue, run);
    }

    [Fact]
    public async Task A_run_is_linked_to_an_issue_listed_with_its_outcome_and_found_again_from_the_run()
    {
        var (team, project, issue, run) = await SetUpAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).StatusCode);

        var linked = Assert.Single((await team.Owner.GetJsonAsync($"/api/issues/{issue}/runs")).EnumerateArray());
        Assert.Equal((run, "pending_approval", "laptop-1"), (linked.GetProperty("runId").GetGuid(), linked.GetProperty("status").GetString(), linked.GetProperty("targetHostname").GetString()));
        Assert.DoesNotContain("hush-command", linked.GetRawText(), StringComparison.Ordinal);
        var back = Assert.Single((await team.Other.GetJsonAsync($"/api/runs/{run}/issues")).EnumerateArray());
        Assert.Equal((issue, project, 1, "Failing build"), (back.GetProperty("issueId").GetGuid(), back.GetProperty("projectId").GetGuid(), back.GetProperty("number").GetInt32(), back.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task A_second_link_an_unknown_run_and_a_run_of_another_workspace_are_refused()
    {
        var (team, _, issue, run) = await SetUpAsync();
        var (otherTeam, _, _, foreignRun) = await SetUpAsync();
        (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).EnsureSuccessStatusCode();

        Assert.Equal(("runId", "duplicate_link"), await ProjectKit.ProblemAsync(await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })));
        Assert.Equal(("runId", "invalid_run"), await ProjectKit.ProblemAsync(await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = Guid.NewGuid() })));
        Assert.Equal(("runId", "invalid_run"), await ProjectKit.ProblemAsync(await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = foreignRun })));
        Assert.Equal(("runId", "invalid_run"), await ProjectKit.ProblemAsync(await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { })));
        Assert.NotNull(otherTeam);
    }

    [Fact]
    public async Task Only_a_worker_of_the_project_links_or_unlinks_and_a_stranger_sees_nothing()
    {
        var (team, project, issue, run) = await SetUpAsync();
        var stranger = await api.NewClient().SignedUpAsync("ir-stranger");
        (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).EnsureSuccessStatusCode();

        // team.Owner is a workspace member with no role in the project
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Owner.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}/runs/{run}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/issues/{issue}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/runs/{run}/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).StatusCode);

        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{team.Owner.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        var second = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/issues/{issue}/runs", new { runId = second })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}/runs/{run}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}/runs/{run}")).StatusCode);
        Assert.Equal([second], (await team.Admin.GetJsonAsync($"/api/issues/{issue}/runs")).EnumerateArray().Select(r => r.GetProperty("runId").GetGuid()));
    }

    [Fact]
    public async Task An_archived_issue_takes_no_new_run_and_leaves_the_run_s_issue_list()
    {
        var (team, _, issue, run) = await SetUpAsync();
        (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).EnsureSuccessStatusCode();
        (await team.Admin.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}")).EnsureSuccessStatusCode();

        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = Guid.NewGuid() })));
        Assert.Empty((await team.Admin.GetJsonAsync($"/api/runs/{run}/issues")).EnumerateArray());
    }

    [Fact]
    public async Task Run_links_are_audited_by_issue_id_without_the_command()
    {
        var (team, _, issue, run) = await SetUpAsync();
        (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).EnsureSuccessStatusCode();
        (await team.Admin.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}/runs/{run}")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.Action == "issue.run_link_changed" && a.TargetId == issue.ToString()).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain("hush", r.Detail?.RootElement.GetRawText() ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_deletes_an_old_run_together_with_its_issue_link_and_the_sweep_does_not_fail()
    {
        var (team, _, issue, run) = await SetUpAsync();
        (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).EnsureSuccessStatusCode();
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromDays(3));
        await RemoteKit.HousekeepAsync(api);
        await RemoteKit.HousekeepAsync(api);

        await using var db = api.Db();
        Assert.False(await db.RemoteRuns.AnyAsync(r => r.Id == run));
        Assert.False(await db.IssueRuns.AnyAsync(l => l.RunId == run));
        Assert.Empty((await team.Admin.GetJsonAsync($"/api/issues/{issue}/runs")).EnumerateArray());
    }
}
