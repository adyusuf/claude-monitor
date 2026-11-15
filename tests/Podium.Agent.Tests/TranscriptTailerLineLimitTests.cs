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
}
