using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>A transcript line longer than the limit is not dropped: it becomes a marker event with its size and a preview.</summary>
public sealed class TranscriptTailerLineLimitTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;

    public TranscriptTailerLineLimitTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private void Track(string session, string content)
    {
        var path = Path.Combine(home.Dir, session + ".jsonl");
        File.WriteAllText(path, content);
        store.TrackTranscript(new TranscriptCursor(session, HarnessKinds.ClaudeCode, path, 0, "k", "n", "main"), clock.GetUtcNow());
    }

    private List<CapturedEvent> Queued() => store.NextBatch(1000, int.MaxValue)?.Rows.Select(r => r.Event).ToList() ?? [];

    [Fact]
    public void A_transcript_line_over_the_limit_becomes_a_marker_event()
    {
        var config = home.Config with { TranscriptLineMax = 100 };
        var longLine = "{\"type\":\"user\",\"text\":\"" + new string('x', 300) + "\"}";
        Track("s1", "{\"type\":\"user\",\"n\":1}\n" + longLine + "\n{\"type\":\"user\",\"n\":3}\n");

        Assert.Equal(3, new TranscriptTailer(config, store, clock, new AgentLog(config, clock)).RunOnce());

        var events = Queued();
        Assert.Equal(3, events.Count);
        Assert.Equal(EventKinds.Transcript, events[1].Kind);
        Assert.True(events[1].Truncated);
        Assert.True(events[1].Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(longLine.Length, events[1].Payload.GetProperty("bytes").GetInt32());
        Assert.StartsWith("{\"type\":\"user\"", events[1].Payload.GetProperty("preview").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, events[2].Payload.GetProperty("n").GetInt32()); // the lines after it are still read
    }

    [Theory]
    [InlineData("\\\"password\\\":\\\"hunterhunterhunter\\\"", "hunterhunterhunter")]
    [InlineData("\\\"api_key\\\": \\\"plainwordplainword\\\"", "plainwordplainword")]
    [InlineData("\\\\\\\"api_key\\\\\\\": \\\\\\\"plainwordplainword\\\\\\\"", "plainwordplainword")] // escaped twice (a transcript inside a tool result inside a transcript)
    public void The_preview_of_an_over_long_line_does_not_keep_a_secret_that_sits_in_escaped_json(string fragment, string value)
    {
        // The line is JSONL: nested content has escaped quotes, which a masker looking for key"..:.. would not see unless they are undone first.
        var config = home.Config with { TranscriptLineMax = 400 };
        var longLine = "{\"type\":\"user\",\"text\":\"{" + fragment + "}" + new string('x', 800) + "\"}";
        Track("s1", longLine + "\n");

        Assert.Equal(1, new TranscriptTailer(config, store, clock, new AgentLog(config, clock)).RunOnce());

        var preview = Queued()[0].Payload.GetProperty("preview").GetString()!;
        Assert.DoesNotContain(value, preview, StringComparison.Ordinal);
        Assert.Contains("[masked:", preview, StringComparison.Ordinal);
    }

    private static string EscapeOnce(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public void A_secret_nested_four_or_more_levels_deep_is_masked_too(int levels)
    {
        // Content inside content is escaped once per level (Q-1 follow-up, F-345): the preview unescapes until the text stops changing.
        var inner = "{\"api_key\": \"plainwordplainword\"}";
        for (var i = 0; i < levels; i++) inner = EscapeOnce(inner);
        var config = home.Config with { TranscriptLineMax = 2000 };
        Track("s1", "{\"type\":\"user\",\"text\":\"" + inner + new string('x', 4000) + "\"}\n");

        Assert.Equal(1, new TranscriptTailer(config, store, clock, new AgentLog(config, clock)).RunOnce());

        var preview = Queued()[0].Payload.GetProperty("preview").GetString()!;
        Assert.DoesNotContain("plainwordplainword", preview, StringComparison.Ordinal);
        Assert.Contains("[masked:", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_beyond_the_cap_keeps_no_preview_text_at_all()
    {
        // "\\u005c" followed by n-1 times "u005c" needs n passes to settle: each pass undoes one escape of a backslash.
        var deep = "\\u005c" + string.Concat(Enumerable.Repeat("u005c", TranscriptTailer.UnescapeLevelsMax + 8));
        var config = home.Config with { TranscriptLineMax = 1000 };
        Track("s1", "{\"text\":\"api_key=plainwordplainword " + deep + new string('x', 2000) + "\"}\n");

        Assert.Equal(1, new TranscriptTailer(config, store, clock, new AgentLog(config, clock)).RunOnce());

        var preview = Queued()[0].Payload.GetProperty("preview").GetString()!;
        Assert.DoesNotContain("plainwordplainword", preview, StringComparison.Ordinal);
        Assert.Equal(TranscriptTailer.NestedTooDeepMark, preview);
    }

    [Fact]
    public void Unescaping_a_fragment_undoes_the_json_escapes_and_leaves_a_cut_or_unknown_escape_alone()
    {
        Assert.Equal("a\"b\\c/d\ne\tf\rg\bh\fi", TranscriptTailer.UnescapeJson("a\\\"b\\\\c\\/d\\ne\\tf\\rg\\bh\\fi"));
        Assert.Equal("A\"", TranscriptTailer.UnescapeJson("\\u0041\\u0022"));
        Assert.Equal("C:\\x\\u00", TranscriptTailer.UnescapeJson("C:\\x\\u00")); // \x is no escape; a cut \u00 stays
        Assert.Equal("end\\", TranscriptTailer.UnescapeJson("end\\")); // a fragment cut right after a backslash
    }
}
