using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The Subagents box also follows the call that starts an agent, for a harness whose SubagentStart and SubagentStop hooks do not arrive.</summary>
[Collection(ApiGroup.Name)]
public sealed class SubagentToolTests(ApiFactory api)
{
    private DateTimeOffset T(int seconds) => api.Clock.GetUtcNow().AddSeconds(seconds);

    private async Task<(TestUser User, TestAgent Agent, string Session)> StartAsync(string name)
    {
        var user = await api.NewClient().SignedUpAsync(name);
        return (user, await user.ConnectAgentAsync(), "sess-" + Guid.NewGuid());
    }

    private static async Task<List<(string Type, string? Description, string Status)>> RunsAsync(TestUser user)
    {
        var id = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await user.GetJsonAsync($"/api/sessions/{id}");
        return [.. detail.GetProperty("subagents").EnumerateArray().Select(r => (r.GetProperty("agentType").GetString()!,
            r.GetProperty("description").ValueKind == JsonValueKind.String ? r.GetProperty("description").GetString() : null, r.GetProperty("status").GetString()!))];
    }

    private static async Task Accepted(TestAgent agent, params ClaudeMonitor.Contracts.CapturedEvent[] events)
    {
        var response = await agent.SendAsync(events);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_finished_agent_call_shows_as_a_finished_subagent_with_its_kind_and_description_even_without_the_hooks()
    {
        var (user, agent, s) = await StartAsync("sub-call");
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", new { tool_name = "Agent", tool_use_id = "toolu_1", tool_input = new { subagent_type = "analyst", description = "Find the callers", prompt = "..." }, tool_response = "done" }, T(0)));

        Assert.Equal([("analyst", "Find the callers", "finished")], await RunsAsync(user));
    }

    [Fact]
    public async Task A_run_the_hooks_reported_and_the_call_names_by_its_agent_id_stays_one_row()
    {
        var (user, agent, s) = await StartAsync("sub-merge");
        await Accepted(agent,
            TestAgent.Hook(s, "SubagentStart", new { agent_id = "a1", agent_type = "analyst" }, T(0)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "Task", tool_use_id = "toolu_2", tool_input = new { subagent_type = "general-purpose", description = "Check it" }, tool_response = new { agentId = "a1" } }, T(1)));

        Assert.Equal([("analyst", "Check it", "finished")], await RunsAsync(user)); // the kind the hook named is kept
    }

    [Fact]
    public async Task An_agent_started_in_the_background_keeps_running_until_its_own_stop()
    {
        var (user, agent, s) = await StartAsync("sub-background");
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", new { tool_name = "Agent", tool_response = new { agentId = "bg1" }, tool_input = new { subagent_type = "builder", description = "Build", run_in_background = true } }, T(0)));
        Assert.Equal([("builder", "Build", "running")], await RunsAsync(user));

        await Accepted(agent, TestAgent.Hook(s, "SubagentStop", new { agent_id = "bg1", agent_type = "builder" }, T(1)));

        Assert.Equal([("builder", "Build", "finished")], await RunsAsync(user));
    }

    [Fact]
    public async Task A_call_with_no_id_a_nested_agents_call_and_odd_shapes_make_no_row()
    {
        var (user, agent, s) = await StartAsync("sub-ignored");
        await Accepted(agent, TestAgent.Hook(s, "UserPromptSubmit", new { prompt = "hello" }, T(0))); // the session exists
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", new { tool_name = "Agent", tool_input = new { subagent_type = "x" } }, T(1))); // nothing names it
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", new { tool_name = "Agent", agent_id = "outer", tool_use_id = "toolu_3", tool_input = new { subagent_type = "y" } }, T(2)));
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", new { tool_name = "Agent", tool_use_id = new string('x', 300), tool_input = new { subagent_type = "z" } }, T(3)));
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", new { tool_name = "Agent", tool_use_id = "toolu_4", tool_input = "not an object" }, T(4)));

        Assert.Empty(await RunsAsync(user));
    }
}
