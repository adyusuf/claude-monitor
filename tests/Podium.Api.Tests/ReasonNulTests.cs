using System.Net;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// A reason the owner types on the web is one line of text: a NUL (which Postgres text cannot hold) is refused with 400 on a run's deny and on a
/// permission answer, as it is on run creation, grants and jobs (F-161), and nothing is saved.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ReasonNulTests(ApiFactory api)
{
    [Fact]
    public async Task A_deny_reason_with_a_NUL_is_refused_and_the_run_stays_waiting()
    {
        var team = await RemoteKit.TeamAsync(api);
        var id = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "tests"))).Id;

        var response = await team.Owner.PostAsync($"/api/runs/{id}/deny", new { reason = "no\0thanks" });

        await RemoteKit.AssertInvalidAsync(response, "reason", RemoteChecks.InvalidReason);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_permission_answer_reason_with_a_NUL_is_refused_and_the_request_stays_open()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "nul");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });

        var response = await w.Owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = PermissionDecisions.Deny, reason = "no\0thanks" });

        await RemoteKit.AssertInvalidAsync(response, "reason", RemoteChecks.InvalidReason);
        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
        Assert.Equal(HttpStatusCode.NoContent,
            (await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, PermissionDecisions.Allow)).StatusCode); // the owner can still answer
    }
}
