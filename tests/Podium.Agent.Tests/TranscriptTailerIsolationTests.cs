using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>One transcript the tailer cannot read, or one line it cannot take, never stops the other transcripts or replays lines already taken.</summary>
public sealed class TranscriptTailerIsolationTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;

    public TranscriptTailerIsolationTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private string Track(string session, string content)
    {
        var path = Path.Combine(home.Dir, session + ".jsonl");
        File.WriteAllText(path, content);
        store.TrackTranscript(new TranscriptCursor(session, HarnessKinds.ClaudeCode, path, 0, "k", "n", "main"), clock.GetUtcNow());
        return path;
    }

    private List<CapturedEvent> Queued() => store.NextBatch(1000, int.MaxValue)?.Rows.Select(r => r.Event).ToList() ?? [];

    [Fact]
    public void One_unreadable_transcript_does_not_stop_the_others()
    {
        if (OperatingSystem.IsWindows()) return; // no unix mode there
        var locked = Track("a-locked", "{\"type\":\"user\"}\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        Track("b-readable", "{\"type\":\"user\"}\n{\"type\":\"summary\"}\n");
        try
        {
            Assert.Equal(2, new TranscriptTailer(home.Config, store, clock, new AgentLog(home.Config, clock)).RunOnce());
            Assert.Equal(["b-readable", "b-readable"], Queued().Select(e => e.SessionExternalId));
            var logged = File.ReadAllText(home.Config.LogPath);
            Assert.Contains("transcript of session a-locked not read: UnauthorizedAccessException", logged, StringComparison.Ordinal);
            Assert.DoesNotContain(locked, logged, StringComparison.Ordinal); // never the path
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void A_line_that_cannot_be_taken_is_skipped_and_the_lines_before_it_are_not_queued_again()
    {
        // "type" must be a string; a number made Take throw after the lines before it were queued but before the offset was saved.
        Track("s1", "{\"type\":\"user\",\"n\":1}\n{\"type\":1}\n{\"type\":\"user\",\"n\":3}\n");
        var tailer = new TranscriptTailer(home.Config, store, clock, new AgentLog(home.Config, clock));

        tailer.RunOnce();
        tailer.RunOnce();

        var lines = Queued().Select(e => e.Payload.TryGetProperty("n", out var n) ? n.GetInt32() : 0).ToList();
        Assert.Equal([1, 0, 3], lines); // each line once, in order; the bad one as it was
        Assert.Equal(0, tailer.RunOnce());
    }
}
