using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>What an agent's own clock may do to a session's list position (an event dated ahead counts as now).</summary>
[Collection(ApiGroup.Name)]
public sealed class SessionClockSkewTests(ApiFactory api)
{
    private static async Task<JsonElement> SessionOf(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0];

    [Fact]
    public async Task An_event_dated_far_in_the_future_does_not_pin_the_session_at_the_top_of_the_list()
    {
        var user = await api.NewClient().SignedUpAsync("skew");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var now = api.Clock.GetUtcNow();

        await agent.SendAsync(TestAgent.Hook(s, "PreToolUse", new { tool_name = "Bash" }, now.AddDays(30)));

        var row = await SessionOf(user);
        Assert.True(row.GetProperty("lastEventAt").GetDateTimeOffset() <= now + TimeSpan.FromMinutes(2),
            "the future-dated event moved LastEventAt past now plus the allowed clock skew");
        Assert.True(row.GetProperty("startedAt").GetDateTimeOffset() <= now + TimeSpan.FromMinutes(2));
        var events = (await user.GetJsonAsync($"/api/sessions/{row.GetProperty("id").GetGuid()}/events")).GetProperty("items");
        Assert.True(events[0].GetProperty("occurredAt").GetDateTimeOffset() <= now + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task An_event_within_the_allowed_skew_keeps_its_own_time()
    {
        var user = await api.NewClient().SignedUpAsync("skew-ok");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var at = api.Clock.GetUtcNow().AddSeconds(30);

        await agent.SendAsync(TestAgent.Hook(s, "PreToolUse", new { tool_name = "Bash" }, at));

        Assert.Equal(at, (await SessionOf(user)).GetProperty("lastEventAt").GetDateTimeOffset());
    }
}
