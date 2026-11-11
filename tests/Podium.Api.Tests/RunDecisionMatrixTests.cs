using System.Net;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>States and callers of the owner's decisions on a run that the other run tests leave out: a decision after the wait ran out, and an owner who has since become a viewer.</summary>
[Collection(ApiGroup.Name)]
public sealed class RunDecisionMatrixTests(ApiFactory api)
{
    private static async Task<(RunCreate Req, Guid Id)> AskAsync(RemoteTeam team)
    {
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "tests");
        return (req, (await RemoteKit.CreateAsync(team.Requester, req)).Id);
    }

    private static async Task DemoteToViewerAsync(RemoteTeam team, TestUser user) =>
        (await team.Admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{team.WorkspaceId}/members/{user.Id}", new { role = "viewer" })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Denying_a_run_past_its_wait_before_the_housekeeper_has_marked_it_is_still_recorded_as_denied()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (_, id) = await AskAsync(team);
        api.Clock.Advance(TimeSpan.FromMinutes(16));

        var response = await team.Owner.PostAsync($"/api/runs/{id}/deny", new { reason = "too late" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((RunStatuses.Denied, team.Owner.Id), (row.Status, row.DecidedBy));
    }

    [Fact]
    public async Task An_owner_who_was_made_a_viewer_cannot_approve_and_the_run_keeps_waiting()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        await DemoteToViewerAsync(team, team.Owner);

        var response = await RemoteKit.ApproveAsync(team.Owner, id, req);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task An_owner_who_was_made_a_viewer_cannot_deny_and_the_run_keeps_waiting()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (_, id) = await AskAsync(team);
        await DemoteToViewerAsync(team, team.Owner);

        var response = await team.Owner.PostAsync($"/api/runs/{id}/deny", new { reason = "no" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task An_owner_who_was_made_a_viewer_cannot_cancel_and_the_run_keeps_waiting()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (_, id) = await AskAsync(team);
        await DemoteToViewerAsync(team, team.Owner);

        var response = await team.Owner.PostAsync($"/api/runs/{id}/cancel");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }
}
