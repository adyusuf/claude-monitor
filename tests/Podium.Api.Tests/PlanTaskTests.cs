using System.Text.Json;
using Podium.Api.Ingest;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The desktop app keeps a session's steps with its update_plan tool; the page's task list follows it (not only TodoWrite and TaskCreate).</summary>
[Collection(ApiGroup.Name)]
public sealed class PlanTaskTests(ApiFactory api)
{
    private DateTimeOffset T(int seconds) => api.Clock.GetUtcNow().AddSeconds(seconds);

    private static async Task Accepted(TestAgent agent, params Podium.Contracts.CapturedEvent[] events)
    {
        var response = await agent.SendAsync(events);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static object Item(string content, string status) => new { content, status };

    private static object Plan(object input) => new { tool_name = EventProjector.PlanTool, tool_input = input };

    private async Task<(TestUser User, TestAgent Agent, string Session)> StartAsync(string name)
    {
        var user = await api.NewClient().SignedUpAsync(name);
        return (user, await user.ConnectAgentAsync(), "sess-" + Guid.NewGuid());
    }

    private static async Task<List<(string Id, string Subject, string Status)>> TasksAsync(TestUser user)
    {
        var id = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await user.GetJsonAsync($"/api/sessions/{id}");
        return [.. detail.GetProperty("tasks").EnumerateArray().Select(t => (t.GetProperty("externalId").GetString()!, t.GetProperty("subject").GetString()!, t.GetProperty("status").GetString()!))];
    }

    [Fact]
    public async Task A_plan_sent_whole_becomes_the_task_list_in_step_order()
    {
        var (user, agent, s) = await StartAsync("plan-whole");
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", Plan(new { items = new[] { Item("Read the code", "in_progress"), Item("Write it", "pending"), Item("Test it", "pending") } }), T(0)));

        Assert.Equal([("plan-0", "Read the code", "in_progress"), ("plan-1", "Write it", "pending"), ("plan-2", "Test it", "pending")], await TasksAsync(user));
    }

    [Fact]
    public async Task Advance_completes_the_running_step_and_starts_the_next_and_done_and_start_set_steps_by_number()
    {
        var (user, agent, s) = await StartAsync("plan-moves");
        await Accepted(agent,
            TestAgent.Hook(s, "PostToolUse", Plan(new { items = new[] { Item("A", "in_progress"), Item("B", "pending"), Item("C", "pending"), Item("D", "pending") } }), T(0)),
            TestAgent.Hook(s, "PostToolUse", Plan(new { advance = true }), T(1)));
        Assert.Equal(["completed", "in_progress", "pending", "pending"], (await TasksAsync(user)).Select(t => t.Status));

        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", Plan(new { done = new[] { 2 }, start = 4 }), T(2)));

        Assert.Equal(["completed", "completed", "pending", "in_progress"], (await TasksAsync(user)).Select(t => t.Status));
    }

    [Fact]
    public async Task A_shorter_list_drops_the_steps_past_its_end_and_a_longer_one_adds_them()
    {
        var (user, agent, s) = await StartAsync("plan-resize");
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", Plan(new { items = new[] { Item("A", "completed"), Item("B", "in_progress"), Item("C", "pending") } }), T(0)));
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", Plan(new { items = new[] { Item("A", "completed"), Item("B2", "in_progress") } }), T(1)));
        Assert.Equal([("plan-0", "A", "completed"), ("plan-1", "B2", "in_progress")], await TasksAsync(user));

        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", Plan(new { items = new[] { Item("A", "completed"), Item("B2", "completed"), Item("C2", "in_progress"), Item("D", "pending") } }), T(2)));

        Assert.Equal(["A", "B2", "C2", "D"], (await TasksAsync(user)).Select(t => t.Subject));
    }

    [Fact]
    public async Task Wrap_up_checks_off_the_running_step_and_leaves_the_waiting_ones()
    {
        var (user, agent, s) = await StartAsync("plan-wrap");
        await Accepted(agent,
            TestAgent.Hook(s, "PostToolUse", Plan(new { items = new[] { Item("A", "completed"), Item("B", "in_progress"), Item("C", "pending") } }), T(0)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = EventProjector.WrapUpTool, tool_input = new { recap = "Done." } }, T(1)));

        Assert.Equal(["completed", "completed", "pending"], (await TasksAsync(user)).Select(t => t.Status));
    }

    [Fact]
    public async Task A_subagents_own_plan_and_shapes_that_are_not_a_plan_change_nothing()
    {
        var (user, agent, s) = await StartAsync("plan-ignored");
        object[] odd =
        [
            Plan(new { items = new[] { Item("Mine", "in_progress") } }),
            new { tool_name = EventProjector.PlanTool, agent_id = "sub", tool_input = new { items = new[] { Item("Theirs", "pending") } } },
            Plan(new { items = "not a list" }),
            Plan(new { advance = "yes" }),
            Plan(new { done = new[] { "x", "7" } }),
            Plan(new { start = 99 }),
            Plan(new { items = new object[] { new { content = "", status = "pending" }, 5, new { status = "pending" } } }),
        ];
        // One batch each, so a shape that breaks ingestion names itself.
        for (var i = 0; i < odd.Length; i++)
        {
            var response = await agent.SendAsync(TestAgent.Hook(s, "PostToolUse", odd[i], T(i)));
            Assert.True(response.IsSuccessStatusCode, $"event {i}: {(int)response.StatusCode}");
        }

        Assert.Equal([("plan-0", "Mine", "in_progress")], await TasksAsync(user));
    }

    [Fact]
    public async Task An_unknown_status_is_a_waiting_step_and_a_long_plan_is_cut_to_twelve_steps()
    {
        var (user, agent, s) = await StartAsync("plan-limits");
        var many = Enumerable.Range(0, 15).Select(i => Item($"S{i}", i == 0 ? "bogus" : "pending")).ToArray();
        await Accepted(agent, TestAgent.Hook(s, "PostToolUse", Plan(new { items = many }), T(0)));

        var tasks = await TasksAsync(user);
        Assert.Equal(EventProjector.PlanItemsMax, tasks.Count);
        Assert.Equal("pending", tasks[0].Status);
        Assert.Equal("S11", tasks[^1].Subject);
    }
}
