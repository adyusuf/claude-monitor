using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Who may change a session's commands and permission answers: a stranger and a removed member are shown nothing (404), and what they aimed at is left as it was.</summary>
[Collection(ApiGroup.Name)]
public sealed class CommandAccessTests(ApiFactory api)
{
    [Fact]
    public async Task A_user_outside_the_workspace_cannot_cancel_a_command_and_it_stays_queued()
    {
        var team = await SessionKit.TeamAsync(api);
        var command = await SessionKit.QueueStopAsync(team);
        var stranger = await api.NewClient().SignedUpAsync("ca-stranger");

        var response = await stranger.PostAsync($"/api/commands/{command}/cancel");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("queued", await SessionKit.CommandStatusAsync(team.Admin, team.SessionId, command));
    }

    [Fact]
    public async Task A_user_outside_the_workspace_cannot_answer_a_permission_request_and_it_stays_open()
    {
        var team = await SessionKit.TeamAsync(api);
        var request = await SessionKit.AskPermissionAsync(team);
        var stranger = await api.NewClient().SignedUpAsync("ca-stranger");

        var response = await stranger.PostAsync($"/api/permission-requests/{request}/answer", new { decision = "allow" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("open", await SessionKit.PermissionStatusAsync(team.Admin, team.SessionId, request));
    }

    [Fact]
    public async Task A_removed_session_owner_gets_404_cancelling_a_command()
    {
        var team = await SessionKit.TeamAsync(api);
        var command = await SessionKit.QueueStopAsync(team);
        await SessionKit.RemoveMemberAsync(team.Admin, team.Owner);

        var response = await team.Owner.PostAsync($"/api/commands/{command}/cancel");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_removed_session_owner_gets_404_answering_a_permission_request()
    {
        var team = await SessionKit.TeamAsync(api);
        var request = await SessionKit.AskPermissionAsync(team);
        await SessionKit.RemoveMemberAsync(team.Admin, team.Owner);

        var response = await team.Owner.PostAsync($"/api/permission-requests/{request}/answer", new { decision = "allow" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_removed_session_owner_gets_404_sending_a_command()
    {
        var team = await SessionKit.TeamAsync(api);
        await SessionKit.RemoveMemberAsync(team.Admin, team.Owner);

        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "stop" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_removed_member_gets_404_on_the_session_detail()
    {
        var team = await SessionKit.TeamAsync(api);
        var reader = await RemoteKit.MemberAsync(api, team.Admin, "ca-reader", "viewer");
        Assert.Equal(HttpStatusCode.OK, (await reader.Http.GetAsync($"/api/sessions/{team.SessionId}")).StatusCode);
        await SessionKit.RemoveMemberAsync(team.Admin, reader);

        var response = await reader.Http.GetAsync($"/api/sessions/{team.SessionId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_removed_member_gets_404_on_the_session_events()
    {
        var team = await SessionKit.TeamAsync(api);
        var reader = await RemoteKit.MemberAsync(api, team.Admin, "ca-reader", "viewer");
        Assert.Equal(HttpStatusCode.OK, (await reader.Http.GetAsync($"/api/sessions/{team.SessionId}/events")).StatusCode);
        await SessionKit.RemoveMemberAsync(team.Admin, reader);

        var response = await reader.Http.GetAsync($"/api/sessions/{team.SessionId}/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
