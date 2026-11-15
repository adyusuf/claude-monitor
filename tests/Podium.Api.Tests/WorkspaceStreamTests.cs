using System.Net;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// The workspace's live stream is opened only by a member, and it ends once its reader may no longer see the workspace: a removed member, a
/// signed-out or expired login. The check runs at every ping, so these tests use a host whose ping comes every 200 ms.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceStreamTests(ApiFactory api)
{
    private static readonly TimeSpan Ping = TimeSpan.FromMilliseconds(200);

    private static async Task<StreamReader> OpenAsync(HttpClient http, Guid workspaceId)
    {
        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/workspaces/{workspaceId}/stream"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    /// <summary>Reads until the server ends the stream; counts the pings it sent. Fails if the stream is still open after ten seconds.</summary>
    private static async Task<int> PingsUntilEndAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pings = 0;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line == "event: ping") pings++;
        }

        return pings;
    }

    private static async Task ReadPingsAsync(StreamReader reader, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var seen = 0; seen < count;)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("stream ended");
            if (line == "event: ping") seen++;
        }
    }

    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, TestUser Admin, TestUser Member, TestUser OnHost)> SetupAsync(string tag)
    {
        var admin = await api.NewClient().SignedUpAsync(tag + "-admin");
        var member = await RemoteKit.MemberAsync(api, admin, tag + "-member");
        var host = HostKit.WithConfig(api, c => c with { StreamPingEvery = Ping });
        return (host, admin, member, await HostKit.SignInAsync(api, host, member));
    }

    [Fact]
    public async Task A_stranger_cannot_open_the_workspace_stream()
    {
        var owner = await api.NewClient().SignedUpAsync("ws-stream-owner");
        var stranger = await api.NewClient().SignedUpAsync("ws-stream-stranger");
        var response = await stranger.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/workspaces/{owner.WorkspaceId}/stream"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_member_keeps_the_stream_and_its_pings()
    {
        var (host, admin, _, onHost) = await SetupAsync("ws-stream-kept");
        await using (host)
        {
            using var stream = await OpenAsync(onHost.Http, admin.WorkspaceId);
            await ReadPingsAsync(stream, 3);
        }
    }

    [Fact]
    public async Task The_stream_ends_at_the_next_ping_once_the_member_is_removed()
    {
        var (host, admin, member, onHost) = await SetupAsync("ws-stream-removed");
        await using (host)
        {
            using var stream = await OpenAsync(onHost.Http, admin.WorkspaceId);
            await ReadPingsAsync(stream, 1);
            await SessionKit.RemoveMemberAsync(admin, member);
            await PingsUntilEndAsync(stream);
        }
    }

    [Fact]
    public async Task The_stream_ends_at_the_next_ping_once_its_login_is_signed_out()
    {
        var (host, admin, _, onHost) = await SetupAsync("ws-stream-logout");
        await using (host)
        {
            using var stream = await OpenAsync(onHost.Http, admin.WorkspaceId);
            await ReadPingsAsync(stream, 1);
            Assert.Equal(HttpStatusCode.NoContent, (await onHost.PostAsync("/api/auth/logout")).StatusCode);
            await PingsUntilEndAsync(stream);
        }
    }

    [Fact]
    public async Task The_stream_ends_at_the_next_ping_once_its_login_has_expired()
    {
        var (host, admin, member, onHost) = await SetupAsync("ws-stream-expired");
        await using (host)
        {
            using var stream = await OpenAsync(onHost.Http, admin.WorkspaceId);
            await ReadPingsAsync(stream, 1);
            var past = api.Clock.GetUtcNow().AddSeconds(-1);
            await using (var db = api.Db())
            {
                await db.LoginSessions.Where(l => l.UserId == member.Id).ExecuteUpdateAsync(l => l.SetProperty(x => x.ExpiresAt, past));
            }

            await PingsUntilEndAsync(stream);
        }
    }
}
