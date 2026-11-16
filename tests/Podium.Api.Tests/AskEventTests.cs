using System.Net;
using Podium.Api.Remote;
using Podium.Api.Streaming;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>
/// The workspace stream tells a machine page that an ask exists or changed, but only the people allowed to read that ask hear it (its requester and its
/// target's owner, the visibility of <c>AskQueries</c>); another member of the workspace learns nothing, not even that an ask exists (F-142).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AskEventTests(ApiFactory api)
{
    private const string Marker = "ask_test_marker";

    private static string Stream(Guid workspaceId) => $"/api/workspaces/{workspaceId}/stream";

    /// <summary>Reads the stream up to a marker published after the ask; every ask event on the way is returned.</summary>
    private async Task<List<System.Text.Json.JsonElement>> AsksUntilMarkerAsync(StreamReader reader, Guid workspaceId)
    {
        api.Services.GetRequiredService<Broker>().Publish(Broker.Workspace(workspaceId), new StreamMessage(Marker, new { }));
        var asks = new List<System.Text.Json.JsonElement>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? current = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) current = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == AskReports.WorkspaceEvent) asks.Add(System.Text.Json.JsonDocument.Parse(line[6..]).RootElement.Clone());
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == Marker) return asks;
        }

        throw new InvalidOperationException("stream ended before the marker");
    }

    [Fact]
    public async Task A_new_ask_is_published_to_its_owner_only_and_never_to_another_member()
    {
        var team = await AskKit.TeamAsync(api);
        var peer = await RemoteKit.MemberAsync(api, team.User, "ask-peer");
        using var ownerStream = await RemoteKit.OpenAsync(team.User.Http, Stream(team.User.WorkspaceId));
        using var peerStream = await RemoteKit.OpenAsync(peer.Http, Stream(team.User.WorkspaceId));
        await RemoteKit.NextEventAsync(ownerStream, "ready");
        await RemoteKit.NextEventAsync(peerStream, "ready");

        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));

        var seenByOwner = await AsksUntilMarkerAsync(ownerStream, team.User.WorkspaceId);
        var seenByPeer = await AsksUntilMarkerAsync(peerStream, team.User.WorkspaceId);
        var asOwner = Assert.Single(seenByOwner);
        Assert.Equal((created.Id, team.TargetId, AskStatuses.Queued), (asOwner.GetProperty("id").GetGuid(), asOwner.GetProperty("targetAgentId").GetGuid(), asOwner.GetProperty("status").GetString()));
        Assert.DoesNotContain("question", asOwner.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(seenByPeer);
    }

    [Fact]
    public async Task An_answer_a_cancel_and_the_opt_in_turned_off_are_published_to_the_owner_and_not_to_another_member()
    {
        var team = await AskKit.TeamAsync(api);
        var peer = await RemoteKit.MemberAsync(api, team.User, "ask-peer");
        using var ownerStream = await RemoteKit.OpenAsync(team.User.Http, Stream(team.User.WorkspaceId));
        using var peerStream = await RemoteKit.OpenAsync(peer.Http, Stream(team.User.WorkspaceId));
        await RemoteKit.NextEventAsync(ownerStream, "ready");
        await RemoteKit.NextEventAsync(peerStream, "ready");

        var answered = await AskKit.DeliveredAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await AskKit.AnswerAsync(team.Target, answered.Id, "main")).StatusCode);
        var cancelled = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        Assert.Equal(HttpStatusCode.NoContent, (await team.Asker.Http.PostAsync($"/api/agent/asks/{cancelled.Id}/cancel", null)).StatusCode);
        var open = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        await AskKit.AcceptAsync(team.User, team.TargetId, false);

        var seenByOwner = await AsksUntilMarkerAsync(ownerStream, team.User.WorkspaceId);
        var seenByPeer = await AsksUntilMarkerAsync(peerStream, team.User.WorkspaceId);
        Assert.Contains(seenByOwner, e => e.GetProperty("id").GetGuid() == answered.Id && e.GetProperty("status").GetString() == AskStatuses.Answered);
        Assert.Contains(seenByOwner, e => e.GetProperty("id").GetGuid() == cancelled.Id && e.GetProperty("status").GetString() == AskStatuses.Cancelled);
        Assert.Contains(seenByOwner, e => e.GetProperty("id").GetGuid() == open.Id && e.GetProperty("status").GetString() == AskStatuses.Cancelled);
        Assert.Empty(seenByPeer);
    }

    [Fact]
    public async Task The_ask_event_does_not_reach_the_workspace_topic_other_streams_read()
    {
        var team = await AskKit.TeamAsync(api);
        var broker = api.Services.GetRequiredService<Broker>();
        using var workspaceWide = broker.Subscribe(Broker.Workspace(team.User.WorkspaceId));

        await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));

        while (workspaceWide.Reader.TryRead(out var message)) Assert.NotEqual(AskReports.WorkspaceEvent, message.Event);
    }
}
