using System.Text.Json.Nodes;
using Podium.Agent.Capture;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-240 (agent half): a NUL or a lone surrogate in a captured string never reaches the outbox; PostgreSQL would refuse the whole batch it travels in.</summary>
public sealed class RecorderCleanTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;

    public RecorderCleanTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private string Queued() => store.NextBatch(10, int.MaxValue)!.Value.Rows.Single().Event.Payload.GetRawText();

    [Fact]
    public void A_nul_in_a_value_or_a_property_name_is_replaced_before_the_event_is_queued()
    {
        new Recorder(home.Config, store).Record(EventKinds.Note, "s1",
            new JsonObject { ["text"] = "find -print0 gave a\0b", ["k\0ey"] = "x", ["list"] = new JsonArray("c\0d") }, null, clock.GetUtcNow());

        var raw = Queued();
        Assert.DoesNotContain("\\u0000", raw, StringComparison.Ordinal);
        Assert.Contains("a\\uFFFDb", raw, StringComparison.Ordinal);
        Assert.Contains("k\\uFFFDey", raw, StringComparison.Ordinal);
        Assert.Contains("c\\uFFFDd", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_surrogate_is_replaced_and_a_whole_pair_stays()
    {
        new Recorder(home.Config, store).Record(EventKinds.Note, "s1",
            new JsonObject { ["text"] = "lone \uD800 high, lone \uDC00 low, pair 😀 end" }, null, clock.GetUtcNow());

        var text = System.Text.Json.JsonDocument.Parse(Queued()).RootElement.GetProperty("text").GetString()!;
        Assert.Equal("lone � high, lone � low, pair 😀 end", text);
    }

    [Fact]
    public void A_surrogate_escape_that_arrived_as_json_text_is_replaced_too()
    {
        // The hook's input is JSON: "\\ud800" parses to a lone half, and a NUL arrives as "\\u0000".
        var payload = JsonNode.Parse("{\"text\":\"half \\ud800 and nul \\u0000 and pair \\ud83d\\ude00\"}")!;
        new Recorder(home.Config, store).Record(EventKinds.Note, "s1", payload, null, clock.GetUtcNow());

        var text = System.Text.Json.JsonDocument.Parse(Queued()).RootElement.GetProperty("text").GetString()!;
        Assert.Equal("half \uFFFD and nul \uFFFD and pair \uD83D\uDE00", text);
    }

    [Fact]
    public void The_marker_of_an_over_sized_event_is_cleaned_too()
    {
        var config = home.Config with { EventMaxBytesDefault = 100 };
        new Recorder(config, store).Record(EventKinds.Note, "s1",
            new JsonObject { ["tool_name"] = "Ba\0sh", ["text"] = new string('x', 300) }, null, clock.GetUtcNow());

        var marker = System.Text.Json.JsonDocument.Parse(Queued()).RootElement;
        Assert.True(marker.GetProperty("truncated").GetBoolean());
        Assert.Equal("Ba�sh", marker.GetProperty("tool_name").GetString());
        Assert.DoesNotContain("\\u0000", marker.GetProperty("preview").GetString(), StringComparison.Ordinal);
    }
}
