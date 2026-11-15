using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// A run's approval, denial or cancel, retiring an approval key and revoking an approval rule each commit with their audit row, or neither
/// does (F-194): a failed audit insert changes nothing, and an approval's two-step code is not used up by it.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class DecisionAuditTests(ApiFactory api)
{
    private const string AuditInsert = "INSERT INTO audit_events";

    private static async Task<(Guid Id, RunCreate Req)> AskAsync(RemoteTeam team)
    {
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "tests");
        return ((await RemoteKit.CreateAsync(team.Requester, req)).Id, req);
    }

    [Fact]
    public async Task A_run_approval_whose_audit_row_fails_stays_pending_and_can_be_approved_again()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (id, req) = await AskAsync(team);
        var fault = new CommandFault(AuditInsert);
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, team.Owner);
        fault.Error = new DatabaseDownException();

        Assert.Equal(HttpStatusCode.InternalServerError, (await RemoteKit.ApproveAsync(owner, id, req)).StatusCode);

        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
        fault.Error = null;
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(owner, id, req)).StatusCode);
        Assert.Equal(RunStatuses.Approved, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_run_denial_or_cancel_whose_audit_row_fails_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (toDeny, _) = await AskAsync(team);
        var (toCancel, _) = await AskAsync(team);
        var fault = new CommandFault(AuditInsert);
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, team.Owner);
        fault.Error = new DatabaseDownException();

        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/runs/{toDeny}/deny", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/runs/{toCancel}/cancel")).StatusCode);

        Assert.Equal((RunStatuses.PendingApproval, RunStatuses.PendingApproval),
            (await RemoteKit.StatusOfAsync(api, toDeny), await RemoteKit.StatusOfAsync(api, toCancel)));
    }

    [Fact]
    public async Task Retiring_an_approval_key_or_revoking_a_rule_whose_audit_row_fails_changes_nothing()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "audit");
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var keyId = Guid.CreateVersion7();
        await using (var db = api.Db())
        {
            db.ApprovalKeys.Add(new ApprovalKey { Id = keyId, UserId = w.Owner.Id, Name = "laptop", PublicKey = "x", Fingerprint = Guid.NewGuid().ToString("N"), CreatedAt = api.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        var fault = new CommandFault(AuditInsert);
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, w.Owner);
        fault.Error = new DatabaseDownException();

        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.PostAsync($"/api/me/approval-keys/{keyId}/retire")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}")).StatusCode);

        await using var check = api.Db();
        Assert.Null(await check.ApprovalKeys.AsNoTracking().Where(k => k.Id == keyId).Select(k => k.RetiredAt).SingleAsync());
        Assert.Null(Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt);
    }
}
