using System.Text;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-255: what the transcript tailer does with a partial last line, a replaced file, a line that is not JSON and an unreadable path, one case per test.</summary>
public sealed class TranscriptTailerTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;
    private readonly string path;
    private readonly TranscriptTailer tailer;

    public TranscriptTailerTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        path = Path.Combine(home.Dir, "t.jsonl");
        tailer = new TranscriptTailer(home.Config, store, clock, new AgentLog(home.Config, clock));
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private void Track() => store.TrackTranscript(new TranscriptCursor("s1", HarnessKinds.ClaudeCode, path, 0, "k", "n", "main"), clock.GetUtcNow());

    private long Offset() => store.Transcripts(clock.GetUtcNow() - TimeSpan.FromDays(1)).Single().Offset;

    private List<string> Queued() => store.NextBatch(100, int.MaxValue)?.Rows.Select(r => r.Event.Payload.GetRawText()).ToList() ?? [];

    [Fact]
    public void A_partial_last_line_is_not_taken_and_the_offset_stays_before_it_until_it_is_complete()
    {
        const string first = "{\"n\":1}\n";
        File.WriteAllText(path, first + "{\"n\":2");
        Track();

        Assert.Equal(1, tailer.RunOnce());
        Assert.Equal(first.Length, Offset());

        File.AppendAllText(path, "}\n");
        Assert.Equal(1, tailer.RunOnce());
        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], Queued());
        Assert.Equal(0, tailer.RunOnce()); // nothing new: nothing taken twice
    }

    [Fact]
    public void A_file_replaced_by_a_shorter_one_is_read_from_its_start()
    {
        File.WriteAllText(path, "{\"n\":1}\n{\"n\":2}\n{\"n\":3}\n");
        Track();
        Assert.Equal(3, tailer.RunOnce());

        File.WriteAllText(path, "{\"n\":9}\n");
        Assert.Equal(1, tailer.RunOnce());
        Assert.Equal("{\"n\":9}".Length + 1, Offset());
        Assert.Equal("{\"n\":9}", Queued()[^1]);
    }

    [Fact]
    public void A_line_that_is_not_json_or_not_an_object_is_skipped_and_the_offset_moves_past_it()
    {
        var text = "not json at all\n[1,2]\n42\n{\"n\":1}\n";
        File.WriteAllText(path, text);
        Track();

        Assert.Equal(1, tailer.RunOnce());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), Offset());
        Assert.Equal(["{\"n\":1}"], Queued());
        Assert.Equal(0, tailer.RunOnce()); // the skipped lines are not read again
    }

    [Fact]
    public void A_transcript_that_is_gone_takes_nothing_and_keeps_its_offset()
    {
        File.WriteAllText(path, "{\"n\":1}\n");
        Track();
        Assert.Equal(1, tailer.RunOnce());
        var offset = Offset();

        File.Delete(path);
        Assert.Equal(0, tailer.RunOnce());
        Assert.Equal(offset, Offset());
    }
}
