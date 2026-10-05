using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The command list says which recorded message of Claude answered each command (a view over events).</summary>
[Collection(ApiGroup.Name)]
public sealed class CommandReplyTests(ApiFactory api)
{
    private sealed record Setup(TestUser Owner, TestAgent Agent, Guid SessionId, string External);

    private async Task<Setup> NewSessionAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("reply-owner");
        var agent = await owner.ConnectAgentAsync();
        var external = "sess-" + Guid.NewGuid();
        (await agent.SendAsync(TestAgent.Hook(external, "SessionStart", new { }, api.Clock.GetUtcNow()))).EnsureSuccessStatusCode();
        var id = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Setup(owner, agent, id, external);
    }

    /// <summary>Sends a prompt and reports it applied at the API's current time; returns the command's id.</summary>
    private static async Task<Guid> AppliedAsync(Setup s, string body, DateTimeOffset? at = null)
    {
        var created = await s.Owner.PostAsync($"/api/sessions/{s.SessionId}/commands", new { kind = "prompt", body });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.Http.PostAsJsonAsync($"/api/agent/commands/{id}/status", new CommandStatusUpdate("applied", null, at), TestUser.Json)).StatusCode);
        return id;
    }

    private static async Task<DateTimeOffset> AppliedAtAsync(Setup s, Guid id) => (await CommandAsync(s, id)).GetProperty("appliedAt").GetDateTimeOffset();

    private static CapturedEvent Line(string external, object message, DateTimeOffset readAt, DateTimeOffset? writtenAt = null, string type = "assistant") =>
        TestAgent.Of(external, EventKinds.Transcript, new { type, timestamp = (writtenAt ?? readAt).UtcDateTime.ToString("O"), message }, readAt);

    private static object Text(string text) => new { content = new object[] { new { type = "text", text } } };

    private static async Task SendAsync(Setup s, params CapturedEvent[] events) => (await s.Agent.SendAsync(events)).EnsureSuccessStatusCode();

    private static async Task<JsonElement> CommandAsync(Setup s, Guid id) =>
        (await s.Owner.GetJsonAsync($"/api/sessions/{s.SessionId}/commands")).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);

    /// <summary>The id the API gave the transcript event whose first text block is exactly this text.</summary>
    private static async Task<long> EventIdOfAsync(Setup s, string text)
    {
        var page = await s.Owner.GetJsonAsync($"/api/sessions/{s.SessionId}/events?kind=transcript&limit=100");
        return page.GetProperty("items").EnumerateArray().Single(e =>
            e.GetProperty("payload").TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            && c.EnumerateArray().Any(b => b.TryGetProperty("text", out var t) && t.GetString() == text)).GetProperty("id").GetInt64();
    }

    private static void AssertNoReply(JsonElement command)
    {
        Assert.Equal(JsonValueKind.Null, command.GetProperty("replyEventId").ValueKind);
        Assert.Equal(JsonValueKind.Null, command.GetProperty("replyText").ValueKind);
        Assert.False(command.GetProperty("replyMore").GetBoolean());
    }

    [Fact]
    public async Task The_first_assistant_text_after_the_command_is_its_reply_and_nothing_else_counts()
    {
        var s = await NewSessionAsync();
        var applied = await AppliedAsync(s, "Run the tests");
        var t0 = api.Clock.GetUtcNow();
        await SendAsync(s,
            Line(s.External, Text("from before the command"), t0.AddSeconds(-20)),
            Line(s.External, new { content = new object[] { new { type = "thinking", thinking = "hmm" } } }, t0.AddSeconds(1)),
            Line(s.External, new { content = new object[] { new { type = "tool_use", name = "Bash" } } }, t0.AddSeconds(2)),
            Line(s.External, Text("a user line is not a reply"), t0.AddSeconds(3), type: "user"),
            Line(s.External, Text("   "), t0.AddSeconds(4)),
            Line(s.External, Text("All 12 tests pass."), t0.AddSeconds(5)),
            Line(s.External, Text("a later message"), t0.AddSeconds(9)));

        var command = await CommandAsync(s, applied);
        Assert.Equal(await EventIdOfAsync(s, "All 12 tests pass."), command.GetProperty("replyEventId").GetInt64());
        Assert.Equal("All 12 tests pass.", command.GetProperty("replyText").GetString());
        Assert.False(command.GetProperty("replyMore").GetBoolean());
        Assert.Equal("applied", command.GetProperty("status").GetString()); // the stored status is untouched
    }

    [Fact]
    public async Task Without_a_later_assistant_message_there_is_no_reply()
    {
        var s = await NewSessionAsync();
        var applied = await AppliedAsync(s, "Anything?");
        var t0 = api.Clock.GetUtcNow();
        AssertNoReply(await CommandAsync(s, applied));
        await SendAsync(s, Line(s.External, Text("older than the command"), t0.AddSeconds(-5)));
        AssertNoReply(await CommandAsync(s, applied));
    }

    [Fact]
    public async Task A_line_written_before_the_command_but_read_after_it_is_not_the_reply()
    {
        var s = await NewSessionAsync();
        var applied = await AppliedAsync(s, "Look at this");
        var t0 = api.Clock.GetUtcNow();
        await SendAsync(s,
            Line(s.External, Text("narration before the command"), readAt: t0.AddSeconds(2), writtenAt: t0.AddSeconds(-3)),
            Line(s.External, Text("the answer"), readAt: t0.AddSeconds(8), writtenAt: t0.AddSeconds(6)));
        Assert.Equal("the answer", (await CommandAsync(s, applied)).GetProperty("replyText").GetString());
    }

    [Fact]
    public async Task Two_commands_applied_together_share_the_reply_and_later_ones_get_their_own()
    {
        var s = await NewSessionAsync();
        var first = await AppliedAsync(s, "one");
        var second = await AppliedAsync(s, "two"); // the same instant
        var t0 = api.Clock.GetUtcNow();
        await SendAsync(s, Line(s.External, Text("answers one and two"), t0.AddSeconds(5)));
        var shared = await EventIdOfAsync(s, "answers one and two");
        Assert.Equal(shared, (await CommandAsync(s, first)).GetProperty("replyEventId").GetInt64());
        Assert.Equal(shared, (await CommandAsync(s, second)).GetProperty("replyEventId").GetInt64());

        api.Clock.Advance(TimeSpan.FromSeconds(30));
        var third = await AppliedAsync(s, "three");
        AssertNoReply(await CommandAsync(s, third)); // the earlier reply is older than this command
        await SendAsync(s, Line(s.External, Text("answers three"), api.Clock.GetUtcNow().AddSeconds(5)));
        Assert.Equal("answers three", (await CommandAsync(s, third)).GetProperty("replyText").GetString());
        Assert.Equal("answers one and two", (await CommandAsync(s, first)).GetProperty("replyText").GetString());
    }

    [Fact]
    public async Task A_reply_in_another_session_is_not_this_sessions_reply()
    {
        var s = await NewSessionAsync();
        var other = "sess-" + Guid.NewGuid();
        var applied = await AppliedAsync(s, "mine");
        var t0 = api.Clock.GetUtcNow();
        await SendAsync(s, TestAgent.Hook(other, "SessionStart", new { }, t0), Line(other, Text("for the other session"), t0.AddSeconds(3)));
        AssertNoReply(await CommandAsync(s, applied));
    }

    [Fact]
    public async Task Only_an_applied_prompt_has_a_reply_not_a_stop_a_failure_or_one_still_waiting()
    {
        var s = await NewSessionAsync();
        var queued = (await (await s.Owner.PostAsync($"/api/sessions/{s.SessionId}/commands", new { kind = "prompt", body = "not taken" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var stop = (await (await s.Owner.PostAsync($"/api/sessions/{s.SessionId}/commands", new { kind = "stop" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var delivered = (await (await s.Owner.PostAsync($"/api/sessions/{s.SessionId}/commands", new { kind = "prompt", body = "taken, not applied" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.Http.PostAsJsonAsync($"/api/agent/commands/{delivered}/status", new CommandStatusUpdate("delivered", null), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.Http.PostAsJsonAsync($"/api/agent/commands/{stop}/status", new CommandStatusUpdate("applied", null), TestUser.Json)).StatusCode);
        var failed = (await (await s.Owner.PostAsync($"/api/sessions/{s.SessionId}/commands", new { kind = "prompt", body = "the hook failed" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.Http.PostAsJsonAsync($"/api/agent/commands/{failed}/status", new CommandStatusUpdate("failed", "no"), TestUser.Json)).StatusCode);
        await SendAsync(s, Line(s.External, Text("some message"), api.Clock.GetUtcNow().AddSeconds(5)));
        AssertNoReply(await CommandAsync(s, queued));
        AssertNoReply(await CommandAsync(s, delivered));
        AssertNoReply(await CommandAsync(s, failed)); // it has an applied_at, but it never reached the session
        AssertNoReply(await CommandAsync(s, stop)); // applied, but a stop is not answered with a message
    }

    [Fact]
    public async Task A_long_reply_is_cut_at_the_preview_length_without_splitting_a_character()
    {
        var s = await NewSessionAsync();
        var applied = await AppliedAsync(s, "Tell me everything");
        var t0 = api.Clock.GetUtcNow();
        const int preview = 300; // the default of ApiConfig.ReplyPreviewChars
        var emoji = new string('a', preview - 1) + "\U0001F600" + "tail"; // the emoji straddles the cut
        await SendAsync(s, Line(s.External, Text(emoji), t0.AddSeconds(2)));
        var command = await CommandAsync(s, applied);
        Assert.True(command.GetProperty("replyMore").GetBoolean());
        Assert.Equal(new string('a', preview - 1), command.GetProperty("replyText").GetString());

        api.Clock.Advance(TimeSpan.FromMinutes(1));
        var exact = await AppliedAsync(s, "Short one");
        var t1 = api.Clock.GetUtcNow();
        var fits = new string('b', preview);
        await SendAsync(s, Line(s.External, Text(fits), t1.AddSeconds(2)));
        var whole = await CommandAsync(s, exact);
        Assert.False(whole.GetProperty("replyMore").GetBoolean());
        Assert.Equal(fits, whole.GetProperty("replyText").GetString());
    }

    [Fact]
    public async Task The_time_the_agent_reports_is_the_applied_time_only_when_it_is_believable()
    {
        var s = await NewSessionAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(10));
        var now = api.Clock.GetUtcNow();
        Assert.Equal(now.AddSeconds(-7), await AppliedAtAsync(s, await AppliedAsync(s, "reported", now.AddSeconds(-7))));
        Assert.Equal(now, await AppliedAtAsync(s, await AppliedAsync(s, "older agent")));
        Assert.Equal(now, await AppliedAtAsync(s, await AppliedAsync(s, "a clock a little ahead", now.AddSeconds(1)))); // never in the future
        Assert.Equal(now, await AppliedAtAsync(s, await AppliedAsync(s, "a clock far ahead", now.AddHours(1))));
        Assert.Equal(now, await AppliedAtAsync(s, await AppliedAsync(s, "before it existed", now.AddHours(-1))));
    }

    [Fact]
    public async Task A_quick_reply_belongs_to_the_command_when_the_agent_says_when_it_was_applied()
    {
        var s = await NewSessionAsync();
        var t0 = api.Clock.GetUtcNow();
        api.Clock.Advance(TimeSpan.FromSeconds(10)); // the daemon reports on its next round, the answer is already written
        var knowing = await AppliedAsync(s, "reported time", t0.AddSeconds(1));
        var guessing = await AppliedAsync(s, "older agent"); // the API's own time: 10 seconds in
        await SendAsync(s, Line(s.External, Text("a quick answer"), readAt: t0.AddSeconds(9), writtenAt: t0.AddSeconds(4)));
        Assert.Equal("a quick answer", (await CommandAsync(s, knowing)).GetProperty("replyText").GetString());
        AssertNoReply(await CommandAsync(s, guessing));
    }
}
