using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-244: after its agent moved to another workspace, the events of a session recorded in the old one are skipped (counted in the answer),
/// never written across workspaces; a session that starts after the move is stored in the new workspace.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IngestAfterMoveTests(ApiFactory api)
{
    [Fact]
    public async Task Events_of_a_session_in_the_old_workspace_are_skipped_and_a_new_session_is_stored_in_the_new_one()
    {
        var user = await api.NewClient().SignedUpAsync("move-ingest");
        var agent = await user.ConnectAgentAsync();
        var agentId = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents")).EnumerateArray().Single().GetProperty("id").GetGuid();
        var old = "sess-" + Guid.NewGuid();
        var at = api.Clock.GetUtcNow().AddMinutes(-10);
        (await agent.SendAsync(TestAgent.Hook(old, "UserPromptSubmit", new { prompt = "before the move" }, at))).EnsureSuccessStatusCode();
        var second = (await (await user.PostAsync("/api/workspaces", new { name = "Second" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Patch, $"/api/agents/{agentId}", new { workspaceId = second })).StatusCode);

        var fresh = "sess-" + Guid.NewGuid();
        var response = await agent.SendAsync(
            TestAgent.Hook(old, "UserPromptSubmit", new { prompt = "after the move, old session" }, at.AddMinutes(1)),
            TestAgent.Hook(old, "Stop", new { }, at.AddMinutes(2)),
            TestAgent.Hook(fresh, "UserPromptSubmit", new { prompt = "after the move, new session" }, at.AddMinutes(3)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = (await response.Content.ReadFromJsonAsync<BatchAck>(TestUser.Json))!;
        Assert.Equal((1, 2), (ack.Stored, ack.Skipped));
        var firstRows = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items");
        var kept = Assert.Single(firstRows.EnumerateArray());
        Assert.Equal(("before the move", "active"), (kept.GetProperty("title").GetString(), kept.GetProperty("status").GetString()));
        var secondRows = (await user.GetJsonAsync($"/api/workspaces/{second}/sessions")).GetProperty("items");
        Assert.Equal("after the move, new session", Assert.Single(secondRows.EnumerateArray()).GetProperty("title").GetString());

        await using var db = api.Db();
        var oldId = kept.GetProperty("id").GetGuid();
        Assert.Equal([user.WorkspaceId], await db.SessionEvents.Where(e => e.SessionId == oldId).Select(e => e.WorkspaceId).Distinct().ToListAsync());
        Assert.Equal(1, await db.SessionEvents.CountAsync(e => e.SessionId == oldId));
    }

    [Fact]
    public async Task A_batch_with_nothing_to_skip_answers_none_skipped()
    {
        var user = await api.NewClient().SignedUpAsync("move-none");
        var agent = await user.ConnectAgentAsync();
        var response = await agent.SendAsync(TestAgent.Hook("sess-" + Guid.NewGuid(), "SessionStart", new { }, api.Clock.GetUtcNow().AddMinutes(-1)));

        var ack = (await response.Content.ReadFromJsonAsync<BatchAck>(TestUser.Json))!;
        Assert.Equal((1, 0), (ack.Stored, ack.Skipped));
    }
}
