using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// What PostgreSQL cannot store (a NUL, half a surrogate pair) arrives from the agent as a "\u" escape in an event's JSON. Such an event is
/// skipped like any other event that is not valid; the rest of its batch is stored and the batch is not refused.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IngestUnstorableTests(ApiFactory api)
{
    private const string Hole = "HOLE-FOR-RAW-TEXT";

    /// <summary>Posts a batch as raw text, with <paramref name="escaped"/> put verbatim (already a JSON string body) where the hole is.</summary>
    private static async Task<HttpResponseMessage> PostRawAsync(TestAgent agent, long seq, IReadOnlyList<CapturedEvent> events, string escaped)
    {
        var json = JsonSerializer.Serialize(new EventBatch(seq, events), TestUser.Json).Replace(Hole, escaped, StringComparison.Ordinal);
        return await agent.Http.PostAsync("/api/agent/batches", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    [Theory]
    [InlineData("a\\u0000b")]
    [InlineData("\\u0000")]
    [InlineData("half \\ud83d of a pair")]
    [InlineData("a lone low \\ude00 half")]
    [InlineData("swapped \\ude00\\ud83d halves")]
    public async Task An_event_with_a_NUL_or_lone_surrogate_is_skipped_and_the_rest_of_the_batch_is_stored(string escaped)
    {
        var user = await api.NewClient().SignedUpAsync("ingest-nul");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var now = api.Clock.GetUtcNow();
        var response = await PostRawAsync(agent, 1_000_000,
        [
            TestAgent.Hook(s, "SessionStart", new { }, now),
            TestAgent.Hook(s, "UserPromptSubmit", new { prompt = Hole }, now.AddSeconds(1)),
            TestAgent.Of(s, EventKinds.Transcript, new { type = "user", message = new { content = Hole } }, now.AddSeconds(2)),
            TestAgent.Hook(s, "Stop", new { }, now.AddSeconds(3)),
        ], escaped);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new BatchAck(1_000_000, false, 2), await response.Content.ReadFromJsonAsync<BatchAck>(TestUser.Json));
        var row = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0];
        Assert.Equal("idle", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("title").ValueKind);
        var events = await user.GetJsonAsync($"/api/sessions/{row.GetProperty("id").GetGuid()}/events");
        Assert.Equal(["hook:Stop", "hook:SessionStart"], events.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
    }

    [Theory]
    [InlineData("a pair \\ud83d\\ude00 is fine")]
    [InlineData("an escaped backslash before u0000: \\\\u0000")]
    [InlineData("other escapes \\u00e9 \\t \\\" end")]
    public async Task Text_that_postgresql_can_store_is_kept(string escaped)
    {
        var user = await api.NewClient().SignedUpAsync("ingest-ok");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var now = api.Clock.GetUtcNow();
        var response = await PostRawAsync(agent, 1_000_000, [TestAgent.Hook(s, "UserPromptSubmit", new { prompt = Hole }, now)], escaped);

        Assert.Equal(new BatchAck(1_000_000, false, 1), await response.Content.ReadFromJsonAsync<BatchAck>(TestUser.Json));
        var row = (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0];
        Assert.Equal(JsonDocument.Parse($"\"{escaped}\"").RootElement.GetString(), row.GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_event_whose_branch_holds_a_NUL_is_skipped()
    {
        var user = await api.NewClient().SignedUpAsync("ingest-nul-branch");
        var agent = await user.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        var now = api.Clock.GetUtcNow();
        var bad = TestAgent.Hook(s, "SessionStart", new { }, now) with { GitBranch = "main\0x" };
        var response = await agent.SendAsync(bad, TestAgent.Hook(s, "Stop", new { }, now.AddSeconds(1)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<BatchAck>(TestUser.Json))!.Stored);
        Assert.Equal("main", (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("gitBranch").GetString());
    }
}
