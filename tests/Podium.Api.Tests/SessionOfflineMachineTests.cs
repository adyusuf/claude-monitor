using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-123: a session whose machine is offline is not shown as running. Its stored status stays as the hooks left it (nothing is changed in the
/// database); the list, its status filter and the session page show an "active" or "waiting" session of an offline machine as "idle".
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class SessionOfflineMachineTests(ApiFactory api)
{
    private async Task<(TestUser User, TestAgent Agent, Guid AgentId)> UserWithAgentAsync(string tag)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        var agent = await user.ConnectAgentAsync();
        var agentId = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents")).EnumerateArray().Single().GetProperty("id").GetGuid();
        return (user, agent, agentId);
    }

    /// <summary>The agent's last heartbeat moved back past the online window: the machine reads as offline from now on.</summary>
    private async Task SilenceAsync(Guid agentId)
    {
        await using var db = api.Db();
        var longAgo = api.Clock.GetUtcNow().AddHours(-1);
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastHeartbeatAt, longAgo));
    }

    private static async Task<Dictionary<string, string>> StatusesAsync(TestUser user, string filter = "")
    {
        var page = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions{(filter.Length > 0 ? "?status=" + filter : "")}");
        return page.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("title").GetString()!, r => r.GetProperty("status").GetString()!);
    }

    private async Task StartAsync(TestAgent agent, string title, params string[] hooks)
    {
        var s = "sess-" + Guid.NewGuid();
        var at = api.Clock.GetUtcNow().AddMinutes(-1);
        var events = new List<Podium.Contracts.CapturedEvent> { TestAgent.Hook(s, "UserPromptSubmit", new { prompt = title }, at) };
        events.AddRange(hooks.Select((h, i) => TestAgent.Hook(s, h, new { message = "needs you" }, at.AddSeconds(i + 1))));
        (await agent.SendAsync([.. events])).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_online_machine_shows_its_sessions_as_the_hooks_left_them()
    {
        var (user, agent, _) = await UserWithAgentAsync("offline-online");
        await StartAsync(agent, "running");
        await StartAsync(agent, "asking", "Notification");
        await StartAsync(agent, "resting", "Stop");

        Assert.Equal(new Dictionary<string, string> { ["running"] = "active", ["asking"] = "waiting", ["resting"] = "idle" }, await StatusesAsync(user));
        Assert.Equal(["running"], (await StatusesAsync(user, "active")).Keys);
        Assert.Equal(["asking"], (await StatusesAsync(user, "waiting")).Keys);
        Assert.Equal(["resting"], (await StatusesAsync(user, "idle")).Keys);
    }

    [Fact]
    public async Task An_offline_machine_shows_its_active_and_waiting_sessions_as_idle_and_ended_stays_ended()
    {
        var (user, agent, agentId) = await UserWithAgentAsync("offline-off");
        await StartAsync(agent, "running");
        await StartAsync(agent, "asking", "Notification");
        await StartAsync(agent, "resting", "Stop");
        await StartAsync(agent, "done", "SessionEnd");

        await SilenceAsync(agentId);

        Assert.Equal(new Dictionary<string, string> { ["running"] = "idle", ["asking"] = "idle", ["resting"] = "idle", ["done"] = "ended" }, await StatusesAsync(user));
        Assert.Empty(await StatusesAsync(user, "active"));
        Assert.Empty(await StatusesAsync(user, "waiting"));
        Assert.Equal(["asking", "resting", "running"], (await StatusesAsync(user, "idle")).Keys.Order());
        Assert.Equal(["done"], (await StatusesAsync(user, "ended")).Keys);
    }

    [Fact]
    public async Task The_session_page_shows_it_idle_and_the_stored_status_is_not_changed()
    {
        var (user, agent, agentId) = await UserWithAgentAsync("offline-detail");
        await StartAsync(agent, "running");
        var id = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal("active", (await user.GetJsonAsync($"/api/sessions/{id}")).GetProperty("session").GetProperty("status").GetString());

        await SilenceAsync(agentId);

        Assert.Equal("idle", (await user.GetJsonAsync($"/api/sessions/{id}")).GetProperty("session").GetProperty("status").GetString());
        await using var db = api.Db();
        Assert.Equal("active", await db.HarnessSessions.Where(s => s.Id == id).Select(s => s.Status).SingleAsync());
    }

    [Fact]
    public async Task A_machine_that_reports_again_shows_its_session_live_again()
    {
        var (user, agent, agentId) = await UserWithAgentAsync("offline-back");
        await StartAsync(agent, "running");
        await SilenceAsync(agentId);
        Assert.Equal("idle", (await StatusesAsync(user))["running"]);

        (await agent.Http.PostAsync("/api/agent/heartbeat", null)).EnsureSuccessStatusCode();

        Assert.Equal("active", (await StatusesAsync(user))["running"]);
        Assert.Equal(["running"], (await StatusesAsync(user, "active")).Keys);
    }
}
