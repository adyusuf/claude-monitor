using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>When an agent leaves a workspace or a member is removed, the remote work around it ends with it (ADR-0005).</summary>
[Collection(ApiGroup.Name)]
public sealed class RemoteCascadeTests(ApiFactory api)
{
    private sealed record Open(Guid Pending, Guid Approved, Guid Grant, Guid Job);

    private DateTimeOffset Now => api.Clock.GetUtcNow();

    /// <summary>Two waiting-or-approved runs, an active grant, an active job and an open alert, all on the team's target.</summary>
    private async Task<Open> OpenWorkAsync(RemoteTeam team)
    {
        var pending = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var approved = (await RemoteKit.CreateAsync(team.Requester, req)).Id;
        (await RemoteKit.ApproveAsync(team.Owner, approved, req)).EnsureSuccessStatusCode();
        var grant = await RemoteKit.ActiveGrantAsync(team);
        var proposal = new JobProposal(team.TargetId, "Job " + Guid.NewGuid().ToString("N")[..8], ["/opt/ci/run-tests"], "/opt/ci", 60, null);
        var job = await (await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", proposal, TestUser.Json)).Content.ReadFromJsonAsync<JobView>(TestUser.Json);
        (await team.Owner.PostAsync($"/api/jobs/{job!.Id}/approve", new { })).EnsureSuccessStatusCode();
        (await team.Target.Http.PostAsJsonAsync("/api/agent/alerts", new[] { new AlertReport("cpu", "", "open", 95, 90, Now) }, TestUser.Json)).EnsureSuccessStatusCode();
        return new Open(pending, approved, grant.Id, job.Id);
    }

    private async Task AssertEndedAsync(RemoteTeam team, Open work, Guid actor)
    {
        await using var db = api.Db();
        Assert.Equal(RunStatuses.Cancelled, (await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == work.Pending)).Status);
        Assert.Equal(RunStatuses.Cancelled, (await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == work.Approved)).Status);
        var grant = await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == work.Grant);
        Assert.Equal((GrantStatuses.Revoked, actor), (grant.Status, grant.RevokedBy));
        Assert.NotNull(grant.RevokedAt);
        var job = await db.MachineJobs.AsNoTracking().SingleAsync(j => j.Id == work.Job);
        Assert.Equal(JobStatuses.Retired, job.Status);
        Assert.NotNull(job.RetiredAt);
        var alert = await db.MachineAlerts.AsNoTracking().SingleAsync(a => a.AgentId == team.TargetId);
        Assert.Equal(AlertStates.Resolved, alert.State);
    }

    private async Task AssertUntouchedAsync(Open work)
    {
        await using var db = api.Db();
        Assert.Equal(RunStatuses.PendingApproval, (await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == work.Pending)).Status);
        Assert.Equal(RunStatuses.Approved, (await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == work.Approved)).Status);
        Assert.Equal(GrantStatuses.Active, (await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == work.Grant)).Status);
        Assert.Equal(JobStatuses.Active, (await db.MachineJobs.AsNoTracking().SingleAsync(j => j.Id == work.Job)).Status);
    }

    [Fact]
    public async Task Removing_the_target_owner_cancels_their_runs_revokes_their_grants_retires_their_jobs_and_closes_their_alerts()
    {
        var team = await RemoteKit.TeamAsync(api);
        var work = await OpenWorkAsync(team);
        var bystanders = await RemoteKit.TeamAsync(api);
        var others = await OpenWorkAsync(bystanders);
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);

        Assert.Equal(HttpStatusCode.NoContent,
            (await team.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{team.WorkspaceId}/members/{team.Owner.Id}")).StatusCode);

        await AssertEndedAsync(team, work, team.Admin.Id);
        await AssertUntouchedAsync(others);
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.RunCancel, e => e.GetProperty("id").GetGuid() == work.Approved);
    }

    [Fact]
    public async Task Removing_a_requester_cancels_the_runs_they_asked_for_and_revokes_the_grants_they_hold_but_not_others_work()
    {
        var team = await RemoteKit.TeamAsync(api);
        var asker = await RemoteKit.MemberAsync(api, team.Admin, "rc-asker");
        var askerAgent = await asker.ConnectAgentAsync(team.WorkspaceId);
        var askReq = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var asked = (await RemoteKit.CreateAsync(askerAgent, askReq)).Id;
        var approvedReq = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var approved = (await RemoteKit.CreateAsync(askerAgent, approvedReq)).Id;
        (await RemoteKit.ApproveAsync(team.Owner, approved, approvedReq)).EnsureSuccessStatusCode();
        var held = await RemoteKit.RequestGrantAsync(askerAgent, RemoteKit.GrantAsk(team.TargetId));
        (await team.Owner.PostAsync($"/api/grants/{held.Id}/approve", new { })).EnsureSuccessStatusCode();
        var ask = RemoteKit.GrantAsk(team.TargetId, max: 50);
        var direct = await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants",
            new { template = ask.Template.ToArray(), cwd = ask.Cwd, maxTimeoutSeconds = 60, days = 30, granteeUserId = asker.Id });
        var directId = (await direct.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        var work = await OpenWorkAsync(team);

        (await team.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{team.WorkspaceId}/members/{asker.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, asked));
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, approved));
        await using var db = api.Db();
        Assert.Equal(GrantStatuses.Revoked, (await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == held.Id)).Status);
        Assert.Equal(GrantStatuses.Revoked, (await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == directId)).Status);
        Assert.Equal(RunStatuses.PendingApproval, (await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == work.Pending)).Status);
        Assert.Equal(GrantStatuses.Active, (await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == work.Grant)).Status);
        Assert.Equal(JobStatuses.Active, (await db.MachineJobs.AsNoTracking().SingleAsync(j => j.Id == work.Job)).Status);
    }

    [Fact]
    public async Task Revoking_an_agent_ends_the_remote_work_around_it_and_only_around_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var work = await OpenWorkAsync(team);
        var bystanders = await RemoteKit.TeamAsync(api);
        var others = await OpenWorkAsync(bystanders);
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);

        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/agents/{team.TargetId}/revoke")).StatusCode);

        await AssertEndedAsync(team, work, team.Admin.Id);
        await AssertUntouchedAsync(others);
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.RunCancel, e => e.GetProperty("id").GetGuid() == work.Approved);
    }

    [Fact]
    public async Task Revoking_a_requesters_agent_cancels_its_runs_and_the_grants_made_for_that_agent()
    {
        var team = await RemoteKit.TeamAsync(api);
        var work = await OpenWorkAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/agents/{team.RequesterId}/revoke")).StatusCode);
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, work.Pending));
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, work.Approved));
        await using var db = api.Db();
        Assert.Equal(GrantStatuses.Revoked, (await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == work.Grant)).Status);
        Assert.Equal(JobStatuses.Active, (await db.MachineJobs.AsNoTracking().SingleAsync(j => j.Id == work.Job)).Status);
    }

    [Fact]
    public async Task Moving_an_agent_to_another_workspace_ends_its_remote_work_and_it_is_no_longer_a_target_here()
    {
        var team = await RemoteKit.TeamAsync(api);
        var work = await OpenWorkAsync(team);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var created = await RemoteKit.CreateAsync(team.Requester, req);

        var moved = await team.Owner.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = team.Owner.WorkspaceId });
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);

        await AssertEndedAsync(team, work, team.Owner.Id);
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, created.Id));
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, created.Id, req), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, created.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).StatusCode);
    }

    [Fact]
    public async Task A_refused_move_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var work = await OpenWorkAsync(team);
        var outsider = await api.NewClient().SignedUpAsync("rc-foreign");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await team.Owner.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = outsider.WorkspaceId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await team.Admin.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = team.Admin.WorkspaceId })).StatusCode);
        await AssertUntouchedAsync(work);
    }

    [Fact]
    public async Task A_member_who_leaves_on_their_own_ends_their_remote_work_too()
    {
        var team = await RemoteKit.TeamAsync(api);
        var work = await OpenWorkAsync(team);
        Assert.Equal(HttpStatusCode.NoContent,
            (await team.Owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{team.WorkspaceId}/members/{team.Owner.Id}")).StatusCode);
        await AssertEndedAsync(team, work, team.Owner.Id);
    }
}
