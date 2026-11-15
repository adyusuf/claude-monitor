using Podium.Api.Background;
using Podium.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>A command or permission request that runs out of time is announced on the workspace stream, so an open session page updates at once.</summary>
[Collection(ApiGroup.Name)]
public sealed class SessionExpiryStreamTests(ApiFactory api)
{
    private async Task PassAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await Housekeeper.PassAsync(scope.ServiceProvider, api.Clock, CancellationToken.None);
    }

    [Fact]
    public async Task An_expired_command_is_announced_with_its_session_id_and_the_expired_status()
    {
        var team = await SessionKit.TeamAsync(api);
        var command = await SessionKit.QueueStopAsync(team);
        using var stream = await RemoteKit.OpenAsync(team.Admin.Http, $"/api/workspaces/{team.Admin.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(stream, "ready");

        api.Clock.Advance(TimeSpan.FromHours(1));
        await PassAsync();

        var message = await RemoteKit.NextEventAsync(stream, "command", e => e.GetProperty("id").GetGuid() == command);
        Assert.Equal(("expired", team.SessionId), (message.GetProperty("status").GetString(), message.GetProperty("sessionId").GetGuid()));
    }

    [Fact]
    public async Task An_expired_permission_request_is_announced_with_its_session_id_and_the_expired_status()
    {
        var team = await SessionKit.TeamAsync(api);
        var request = await SessionKit.AskPermissionAsync(team);
        using var stream = await RemoteKit.OpenAsync(team.Admin.Http, $"/api/workspaces/{team.Admin.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(stream, "ready");

        api.Clock.Advance(TimeSpan.FromHours(1));
        await PassAsync();

        var message = await RemoteKit.NextEventAsync(stream, "permission", e => e.GetProperty("id").GetGuid() == request);
        Assert.Equal(("expired", team.SessionId), (message.GetProperty("status").GetString(), message.GetProperty("sessionId").GetGuid()));
    }
}
