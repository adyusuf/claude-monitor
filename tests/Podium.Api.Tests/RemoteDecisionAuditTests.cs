using System.Net;
using System.Net.Http.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>A grant or job decision and its audit row commit together, or neither does (ADR-0005): a failed audit insert changes nothing.</summary>
[Collection(ApiGroup.Name)]
public sealed class RemoteDecisionAuditTests(ApiFactory api)
{
    private const string AuditInsert = "INSERT INTO audit_events";

    private async Task<string> GrantStatusAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.MachineGrants.AsNoTracking().Where(g => g.Id == id).Select(g => g.Status).SingleAsync();
    }

    private async Task<string> JobStatusAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.MachineJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.Status).SingleAsync();
    }

    private static async Task<Guid> ProposeAsync(RemoteTeam team, string name)
    {
        var response = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs",
            new JobProposal(team.TargetId, name, ["/opt/ci/run-tests", "--suite", "unit"], "/opt/ci", 300, "needs a test run"), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!.Id;
    }

    [Fact]
    public async Task A_grant_approval_whose_audit_row_fails_stays_requested_and_its_two_step_code_is_not_used_up()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment = "production", function = "application" })).EnsureSuccessStatusCode();
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        var fault = new CommandFault(AuditInsert);
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, team.Owner);
        var secret = await RemoteKit.EnableTotpAsync(api, owner);
        var code = RemoteKit.TotpNow(api, secret);
        fault.Error = new DatabaseDownException();

        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/grants/{grant.Id}/approve", new { code })).StatusCode);

        Assert.Equal(GrantStatuses.Requested, await GrantStatusAsync(grant.Id));
        fault.Error = null;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/grants/{grant.Id}/approve", new { code })).StatusCode);
        Assert.Equal(GrantStatuses.Active, await GrantStatusAsync(grant.Id));
    }

    [Fact]
    public async Task A_grant_denial_or_revocation_whose_audit_row_fails_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var requested = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        var active = await RemoteKit.ActiveGrantAsync(team, RemoteKit.GrantAsk(team.TargetId, max: 50));
        var fault = new CommandFault(AuditInsert);
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, team.Owner);
        fault.Error = new DatabaseDownException();

        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/grants/{requested.Id}/deny")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/grants/{active.Id}/revoke")).StatusCode);

        Assert.Equal((GrantStatuses.Requested, GrantStatuses.Active), (await GrantStatusAsync(requested.Id), await GrantStatusAsync(active.Id)));
    }

    [Fact]
    public async Task A_job_approval_denial_or_retirement_whose_audit_row_fails_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var toApprove = await ProposeAsync(team, "Run tests");
        var toDeny = await ProposeAsync(team, "Run lint");
        var toRetire = await ProposeAsync(team, "Run build");
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{toRetire}/approve", new { })).StatusCode);
        var fault = new CommandFault(AuditInsert);
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, team.Owner);
        fault.Error = new DatabaseDownException();

        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/jobs/{toApprove}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/jobs/{toDeny}/deny")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/jobs/{toRetire}/retire")).StatusCode);

        Assert.Equal((JobStatuses.Proposed, JobStatuses.Proposed, JobStatuses.Active),
            (await JobStatusAsync(toApprove), await JobStatusAsync(toDeny), await JobStatusAsync(toRetire)));
    }
}
