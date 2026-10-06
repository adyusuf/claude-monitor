using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What a grant does once it is active: it approves matching runs at once, and only those (ADR-0005).</summary>
[Collection(ApiGroup.Name)]
public sealed class GrantAndJobTests(ApiFactory api)
{
    private async Task<MachineGrant> GrantRowAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == id);
    }

    [Fact]
    public async Task A_matching_argv_run_is_approved_at_once_with_the_grant_and_its_use_is_counted()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.ActiveGrantAsync(team);
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);

        var created = await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId));
        Assert.Equal((RunStatuses.Approved, grant.Id), (created.Status, created.GrantId));
        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Equal((RunStatuses.Approved, grant.Id, team.Owner.Id), (row.Status, row.GrantId, row.DecidedBy));
        Assert.InRange(row.ExpiresAt - api.Clock.GetUtcNow(), TimeSpan.FromMinutes(15) - TimeSpan.FromMilliseconds(5), TimeSpan.FromMinutes(15));
        var used = await GrantRowAsync(grant.Id);
        Assert.Equal(1, used.UseCount);
        Assert.NotNull(used.LastUsedAt);

        var message = await RemoteKit.RunEventAsync(targetStream, AgentStreamEvents.Run, created.Id);
        Assert.Equal(grant.Id, message.GetProperty("grantId").GetGuid());
        var template = message.GetProperty("grant");
        Assert.Equal(grant.Template, template.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList());
        Assert.Equal("/var/log/app", template.GetProperty("cwd").GetString());
        Assert.Equal(60, template.GetProperty("maxTimeoutSeconds").GetInt32());

        await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId, lines: 100));
        Assert.Equal(2, (await GrantRowAsync(grant.Id)).UseCount);
        var shown = await team.Requester.Http.GetFromJsonAsync<List<GrantView>>("/api/agent/grants", TestUser.Json);
        Assert.Equal(2, shown!.Single(g => g.Id == grant.Id).UseCount);
    }

    [Fact]
    public async Task Before_the_owner_approves_a_requested_grant_matches_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        Assert.Equal(GrantStatuses.Requested, grant.Status);
        var created = await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId));
        Assert.Equal((RunStatuses.PendingApproval, null), (created.Status, created.GrantId));
        Assert.Equal(0, (await GrantRowAsync(grant.Id)).UseCount);
    }

    [Theory]
    [InlineData("tail", "-n", "101", "/var/log/app/api.log", "/var/log/app", 30)]
    [InlineData("tail", "-n", "0", "/var/log/app/api.log", "/var/log/app", 30)]
    [InlineData("tail", "-n", "5", "/var/log/other/api.log", "/var/log/app", 30)]
    [InlineData("tail", "-n", "5", "/var/log/app/../x", "/var/log/app", 30)]
    [InlineData("tail", "-n", "5", "/var/log/app/api.log", "/tmp", 30)]
    [InlineData("tail", "-n", "5", "/var/log/app/api.log", "/var/log/app", 61)]
    [InlineData("head", "-n", "5", "/var/log/app/api.log", "/var/log/app", 30)]
    [InlineData("tail", "-f", "5", "/var/log/app/api.log", "/var/log/app", 30)]
    public async Task A_run_that_does_not_fit_the_grant_stays_waiting_for_its_owner(string program, string flag, string lines, string path,
        string cwd, int timeout)
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.ActiveGrantAsync(team);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), [$"/usr/bin/{program}", flag, lines, path], cwd, timeout);
        var created = await RemoteKit.CreateAsync(team.Requester, req);
        Assert.Equal((RunStatuses.PendingApproval, null), (created.Status, created.GrantId));
        Assert.Equal(0, (await GrantRowAsync(grant.Id)).UseCount);
    }

    [Fact]
    public async Task A_shell_run_never_matches_a_grant_even_with_the_same_words()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        await RemoteKit.ActiveGrantAsync(team);
        var shell = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey(), "/usr/bin/tail -n 5 /var/log/app/api.log");
        var created = await RemoteKit.CreateAsync(team.Requester, shell);
        Assert.Equal((RunStatuses.PendingApproval, null), (created.Status, created.GrantId));
    }

    [Fact]
    public async Task A_grant_belongs_to_its_grantee_and_the_agent_that_asked_for_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.ActiveGrantAsync(team);
        var theirAgent = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        var sameUserOtherAgent = await team.Admin.ConnectAgentAsync();
        foreach (var agent in new[] { theirAgent, sameUserOtherAgent })
        {
            var created = await RemoteKit.CreateAsync(agent, RemoteKit.GrantRun(team.TargetId));
            Assert.Equal((RunStatuses.PendingApproval, null), (created.Status, created.GrantId));
        }

        Assert.Equal(RunStatuses.Approved, (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Status);
        Assert.Equal(1, (await GrantRowAsync(grant.Id)).UseCount);
    }

    [Fact]
    public async Task A_grant_the_owner_gives_to_a_member_works_for_that_members_agents_only()
    {
        var team = await RemoteKit.TeamAsync(api);
        var ask = RemoteKit.GrantAsk(team.TargetId);
        var gave = await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants",
            new { template = ask.Template, cwd = ask.Cwd, maxTimeoutSeconds = ask.MaxTimeoutSeconds, days = 30, granteeUserId = team.Other.Id });
        Assert.Equal(HttpStatusCode.OK, gave.StatusCode);
        var theirAgent = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        var another = await team.Other.ConnectAgentAsync(team.WorkspaceId);

        Assert.Equal(RunStatuses.Approved, (await RemoteKit.CreateAsync(theirAgent, RemoteKit.GrantRun(team.TargetId))).Status);
        Assert.Equal(RunStatuses.Approved, (await RemoteKit.CreateAsync(another, RemoteKit.GrantRun(team.TargetId))).Status);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Status);
    }

    [Fact]
    public async Task Revoking_a_grant_cancels_the_runs_it_approved_tells_the_target_and_approves_nothing_more()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.ActiveGrantAsync(team);
        var approved = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Id;
        var running = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Id;
        (await RemoteKit.StatusAsync(team.Target, running, new RunStatusUpdate("running"))).EnsureSuccessStatusCode();
        var waiting = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId, lines: 500))).Id; // not approved by the grant
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);

        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync($"/api/grants/{grant.Id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/revoke")).StatusCode);
        var row = await GrantRowAsync(grant.Id);
        Assert.Equal((GrantStatuses.Revoked, team.Owner.Id), (row.Status, row.RevokedBy));
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, approved));
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, running));
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, waiting));
        var heard = new[] { await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.RunCancel), await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.RunCancel) };
        Assert.Equal(new[] { approved, running }.Order(), heard.Select(e => e.GetProperty("id").GetGuid()).Order());

        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/revoke"), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        var after = await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId));
        Assert.Equal((RunStatuses.PendingApproval, null), (after.Status, after.GrantId));
    }

    [Fact]
    public async Task The_grantee_may_revoke_their_own_grant_too()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.ActiveGrantAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/grants/{grant.Id}/revoke")).StatusCode);
        Assert.Equal(GrantStatuses.Revoked, (await GrantRowAsync(grant.Id)).Status);
    }

    [Fact]
    public async Task An_expired_grant_stops_matching_and_the_housekeeper_marks_it_expired()
    {
        var team = await RemoteKit.TeamAsync(api);
        var ask = RemoteKit.GrantAsk(team.TargetId);
        var gave = await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants",
            new { template = ask.Template, cwd = ask.Cwd, maxTimeoutSeconds = 60, days = 1, granteeUserId = team.Admin.Id });
        var grantId = (await gave.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(RunStatuses.Approved, (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Status);

        api.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var freshAgent = await team.Admin.ConnectAgentAsync(); // the first agent's access token ran out with the day
        var created = await RemoteKit.CreateAsync(freshAgent, RemoteKit.GrantRun(team.TargetId));
        Assert.Equal((RunStatuses.PendingApproval, null), (created.Status, created.GrantId));
        Assert.Equal(GrantStatuses.Active, (await GrantRowAsync(grantId)).Status);

        await RemoteKit.HousekeepAsync(api);
        Assert.Equal(GrantStatuses.Expired, (await GrantRowAsync(grantId)).Status);
        Assert.Equal(1, (await GrantRowAsync(grantId)).UseCount);
    }
}
