using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class IngestTests(ApiFactory api)
{
    private DateTimeOffset T(int seconds) => api.Clock.GetUtcNow().AddSeconds(seconds);

    private static async Task<JsonElement> SessionOf(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0];

    [Fact]
    public async Task A_batch_is_stored_once_and_folded_into_the_session()
    {
        var user = await api.NewClient().SignedUpAsync("ingest");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var batch = new EventBatch(7,
        [
            TestAgent.Hook(s, "SessionStart", new { session_id = s, model = "claude-opus-5-5", source = "startup" }, T(0)),
            TestAgent.Hook(s, "UserPromptSubmit", new { prompt = "Fix the İstanbul login\nsecond line" }, T(1)),
            TestAgent.Hook(s, "UserPromptSubmit", new { prompt = "A later prompt does not rename it" }, T(2)),
        ]);
        var first = await (await agent.Http.PostAsJsonAsync("/api/agent/batches", batch, TestUser.Json)).Content.ReadFromJsonAsync<BatchAck>(TestUser.Json);
        Assert.Equal(new BatchAck(7, false, 3), first);
        var retry = await (await agent.Http.PostAsJsonAsync("/api/agent/batches", batch, TestUser.Json)).Content.ReadFromJsonAsync<BatchAck>(TestUser.Json);
        Assert.Equal(new BatchAck(7, true, 0), retry);

        var row = await SessionOf(user);
        Assert.Equal("Fix the İstanbul login", row.GetProperty("title").GetString());
        Assert.Equal("active", row.GetProperty("status").GetString());
        Assert.Equal("claude-opus-5-5", row.GetProperty("model").GetString());
        Assert.Equal("Repo Name", row.GetProperty("projectName").GetString());
        Assert.Equal("main", row.GetProperty("gitBranch").GetString());
        Assert.Equal("laptop-1", row.GetProperty("hostname").GetString());
        var events = await user.GetJsonAsync($"/api/sessions/{row.GetProperty("id").GetGuid()}/events");
        Assert.Equal(3, events.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Status_follows_the_hooks_and_ended_stays_ended()
    {
        var user = await api.NewClient().SignedUpAsync("status");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(s, "PreToolUse", new { tool_name = "Bash" }, T(0)));
        Assert.Equal("active", (await SessionOf(user)).GetProperty("status").GetString());
        await agent.SendAsync(TestAgent.Hook(s, "Notification", new { message = "needs you" }, T(1)));
        Assert.Equal("waiting", (await SessionOf(user)).GetProperty("status").GetString());
        await agent.SendAsync(TestAgent.Hook(s, "Stop", new { }, T(2)));
        Assert.Equal("idle", (await SessionOf(user)).GetProperty("status").GetString());
        await agent.SendAsync(TestAgent.Hook(s, "SessionEnd", new { reason = "logout" }, T(3)), TestAgent.Hook(s, "Stop", new { }, T(4)));
        var row = await SessionOf(user);
        Assert.Equal("ended", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, row.GetProperty("endedAt").ValueKind);
        await agent.SendAsync(TestAgent.Hook(s, "SessionStart", new { source = "resume", session_title = "Renamed by user" }, T(5)));
        row = await SessionOf(user);
        Assert.Equal("active", row.GetProperty("status").GetString());
        Assert.Equal("Renamed by user", row.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Task_tools_todo_lists_subagents_and_usage_become_projections()
    {
        var user = await api.NewClient().SignedUpAsync("projections");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        await agent.SendAsync(
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TaskCreate", tool_input = new { subject = "Write the parser" }, tool_response = new { task = new { id = "1" } } }, T(0)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TaskCreate", tool_input = new { subject = "Second" }, tool_response = "Task #2 created successfully" }, T(1)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TaskUpdate", tool_input = new { taskId = "1", status = "completed", subject = "Write the parser fully" } }, T(2)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TaskUpdate", tool_input = new { taskId = 2, status = "deleted" } }, T(3)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TaskUpdate", tool_input = new { taskId = "99", status = "completed" } }, T(4)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TodoWrite", tool_input = new { todos = new[] { new { content = "a", status = "pending" }, new { content = "b", status = "in_progress" } } } }, T(5)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TodoWrite", tool_input = new { todos = new[] { new { content = "a", status = "completed" } } } }, T(6)),
            TestAgent.Hook(s, "PostToolUse", new { tool_name = "TaskCreate", agent_id = "sub", tool_input = new { subject = "subagent's own" }, tool_response = new { task = new { id = "5" } } }, T(7)),
            TestAgent.Hook(s, "SubagentStart", new { agent_id = "a1", agent_type = "analyst" }, T(8)),
            TestAgent.Hook(s, "SubagentStop", new { agent_id = "a1", agent_type = "analyst" }, T(9)),
            TestAgent.Hook(s, "SubagentStop", new { agent_id = "a2" }, T(10)),
            TestAgent.Of(s, "usage", new { model = "claude-opus-5-5", inputTokens = 1_000_000, outputTokens = 100_000, cacheReadTokens = 0, cacheWrite5mTokens = 0, cacheWrite1hTokens = 0 }, T(11)),
            TestAgent.Of(s, "usage", new { model = "claude-opus-5-5", inputTokens = 0, outputTokens = 0, cacheReadTokens = 1_000_000, cacheWrite5mTokens = 1_000_000, cacheWrite1hTokens = 1_000_000 }, T(12)));
        var id = (await SessionOf(user)).GetProperty("id").GetGuid();
        var detail = await user.GetJsonAsync($"/api/sessions/{id}");
        var tasks = detail.GetProperty("tasks").EnumerateArray().Select(t => (t.GetProperty("externalId").GetString(), t.GetProperty("subject").GetString(), t.GetProperty("status").GetString())).ToList();
        Assert.Equal([("1", "Write the parser fully", "completed"), ("todo-0", "a", "completed")], tasks);
        var runs = detail.GetProperty("subagents").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.GetProperty("agentType").GetString() == "analyst" && r.GetProperty("status").GetString() == "finished");
        Assert.Contains(runs, r => r.GetProperty("agentType").GetString() == "general-purpose");
        var usage = detail.GetProperty("usage")[0];
        Assert.Equal(1_000_000, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(2_000_000, usage.GetProperty("cacheWriteTokens").GetInt64());
        // 4 + 100k*20/1M (2) + cache read 0.2 + 5m write 5 + 1h write 8 = 19.2
        Assert.Equal(19.2m, usage.GetProperty("costUsd").GetDecimal());
        Assert.Equal(19.2m, detail.GetProperty("session").GetProperty("costUsd").GetDecimal());
        Assert.True(detail.GetProperty("canCommand").GetBoolean());
    }

    [Fact]
    public async Task An_unpriced_model_has_no_cost_rather_than_a_guess()
    {
        var user = await api.NewClient().SignedUpAsync("unpriced");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Of(s, "usage", new { model = "mystery-model", inputTokens = 10 }, T(0)),
            TestAgent.Of(s, "usage", new { inputTokens = 10 }, T(1)));
        Assert.Equal(JsonValueKind.Null, (await SessionOf(user)).GetProperty("costUsd").ValueKind);
    }

    [Fact]
    public async Task Invalid_events_are_skipped_and_oversized_payloads_become_markers()
    {
        var user = await api.NewClient().SignedUpAsync("invalid");
        await user.SendAsync(HttpMethod.Put, $"/api/workspaces/{user.WorkspaceId}/settings", new { eventMaxBytes = 1024 });
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var big = TestAgent.Hook(s, "PostToolUse", new { tool_response = new string('x', 5000) }, T(0));
        var ack = await (await agent.SendAsync(
                big,
                new CapturedEvent("other_harness", s, "hook:Stop", T(1), JsonSerializer.SerializeToElement(new { })),
                new CapturedEvent(HarnessKinds.ClaudeCode, "", "hook:Stop", T(1), JsonSerializer.SerializeToElement(new { })),
                new CapturedEvent(HarnessKinds.ClaudeCode, s, "", T(1), JsonSerializer.SerializeToElement(new { })),
                new CapturedEvent(HarnessKinds.ClaudeCode, s, "hook:Stop", T(1), JsonSerializer.SerializeToElement(new[] { 1 }))))
            .Content.ReadFromJsonAsync<BatchAck>(TestUser.Json);
        Assert.Equal(1, ack!.Stored);
        var id = (await SessionOf(user)).GetProperty("id").GetGuid();
        var e = (await user.GetJsonAsync($"/api/sessions/{id}/events")).GetProperty("items")[0];
        Assert.True(e.GetProperty("truncated").GetBoolean());
        Assert.True(e.GetProperty("payload").GetProperty("truncated").GetBoolean());
        Assert.True(e.GetProperty("payload").GetProperty("bytes").GetInt32() > 5000);
    }

    [Fact]
    public async Task A_batch_with_too_many_events_is_refused_whole()
    {
        var agent = await (await api.NewClient().SignedUpAsync("too-many")).ConnectAgentAsync();
        var events = Enumerable.Range(0, 501).Select(i => TestAgent.Hook("s", "Stop", new { }, T(i))).ToArray();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await agent.SendAsync(events)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/agent/batches", new EventBatch(1, []))).StatusCode);
    }

    [Fact]
    public async Task Sessions_page_newest_first_and_search_ignores_case_and_accents()
    {
        var user = await api.NewClient().SignedUpAsync("paging");
        var agent = await user.ConnectAgentAsync();
        for (var i = 0; i < 5; i++)
        {
            await agent.SendAsync(TestAgent.Hook($"s{i}", "UserPromptSubmit", new { prompt = i == 3 ? "Şişman ÇAĞRI" : $"prompt {i}" }, T(i * 10), project: i == 1 ? "Ünal-Repo" : "repo"));
        }

        var page1 = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?limit=2");
        Assert.Equal(["prompt 4", "Şişman ÇAĞRI"], page1.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        var next = page1.GetProperty("next").GetString();
        var page2 = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?limit=2&cursor={next}");
        Assert.Equal(["prompt 2", "prompt 1"], page2.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        var page3 = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?limit=2&cursor={page2.GetProperty("next").GetString()}");
        Assert.Equal(JsonValueKind.Null, page3.GetProperty("next").ValueKind);

        var found = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?q=sisman%20cagri");
        Assert.Single(found.GetProperty("items").EnumerateArray());
        var byProject = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?q=unal");
        Assert.Single(byProject.GetProperty("items").EnumerateArray());
        var wildcard = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?q=%25");
        Assert.Empty(wildcard.GetProperty("items").EnumerateArray());
        var active = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?status=ended");
        Assert.Empty(active.GetProperty("items").EnumerateArray());
        var badCursor = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?cursor=garbage&limit=500");
        Assert.Equal(5, badCursor.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Events_page_backwards_and_filter_by_kind_and_strangers_see_nothing()
    {
        var user = await api.NewClient().SignedUpAsync("events");
        var agent = await user.ConnectAgentAsync();
        await agent.SendAsync(Enumerable.Range(0, 5).Select(i => TestAgent.Hook("s", i % 2 == 0 ? "Stop" : "PreToolUse", new { i }, T(i))).ToArray());
        var id = (await SessionOf(user)).GetProperty("id").GetGuid();
        var first = await user.GetJsonAsync($"/api/sessions/{id}/events?limit=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var rest = await user.GetJsonAsync($"/api/sessions/{id}/events?limit=10&before={first.GetProperty("next").GetString()}");
        Assert.Equal(3, rest.GetProperty("items").GetArrayLength());
        var stops = await user.GetJsonAsync($"/api/sessions/{id}/events?kind=hook:Stop");
        Assert.Equal(3, stops.GetProperty("items").GetArrayLength());
        var both = await user.GetJsonAsync($"/api/sessions/{id}/events?kind=hook:Stop,%20hook:PreToolUse,,hook:Stop");
        Assert.Equal(5, both.GetProperty("items").GetArrayLength());
        var several = await user.GetJsonAsync($"/api/sessions/{id}/events?kind=hook:PreToolUse,hook:Nothing");
        Assert.Equal(2, several.GetProperty("items").GetArrayLength());
        var empty = await user.GetJsonAsync($"/api/sessions/{id}/events?kind=,");
        Assert.Equal(5, empty.GetProperty("items").GetArrayLength());

        var stranger = await api.NewClient().SignedUpAsync("events-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/sessions/{id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync($"/api/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_heartbeat_records_the_agent_version_and_time()
    {
        var user = await api.NewClient().SignedUpAsync("heartbeat");
        var agent = await user.ConnectAgentAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        var row = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents"))[0];
        Assert.Equal("0.3.0", row.GetProperty("version").GetString());
        var settings = await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json);
        Assert.Equal(new AgentSettings(true, 262_144, user.WorkspaceId, UpdateModes.Off, false, new AlertThresholds(90, 90, 90, 300)), settings);
    }
}
