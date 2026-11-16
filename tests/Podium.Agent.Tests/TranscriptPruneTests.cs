using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-313: the transcript rows and usage ids of sessions that finished and whose file is gone are forgotten after the retention; nothing else is.</summary>
public sealed class TranscriptPruneTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;
    private readonly TranscriptTailer tailer;

    public TranscriptPruneTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        tailer = new TranscriptTailer(home.Config, store, clock, new AgentLog(home.Config, clock));
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    /// <summary>A session last mentioned by a hook <paramref name="idle"/> ago, its file there or not, one message of its usage counted.</summary>
    private string Session(string name, TimeSpan idle, bool fileExists)
    {
        var path = Path.Combine(home.Dir, name + ".jsonl");
        if (fileExists) File.WriteAllText(path, "");
        store.TrackTranscript(new TranscriptCursor(name, HarnessKinds.ClaudeCode, path, 0, "k", "n", "main"), clock.GetUtcNow() - idle);
        Assert.True(store.FirstSight(name, "m1"));
        return name;
    }

    private List<string> Kept() => store.Transcripts(DateTimeOffset.MinValue).Select(t => t.Session).Order(StringComparer.Ordinal).ToList();

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    [Fact]
    public void An_idle_session_whose_file_is_gone_is_forgotten_with_its_usage_ids_and_the_others_stay()
    {
        Session("gone-old", Retention + TimeSpan.FromDays(1), fileExists: false);
        Session("file-old", Retention + TimeSpan.FromDays(1), fileExists: true);
        Session("gone-recent", Retention - TimeSpan.FromDays(1), fileExists: false);

        tailer.RunOnce();

        Assert.Equal(["file-old", "gone-recent"], Kept());
        Assert.True(store.FirstSight("gone-old", "m1")); // its ids went with it: counted as new again
        Assert.False(store.FirstSight("file-old", "m1")); // a session whose file is still there keeps them (a late hook would otherwise send the whole file again)
        Assert.False(store.FirstSight("gone-recent", "m1"));
    }

    [Fact]
    public void The_retention_is_never_shorter_than_the_window_the_tailer_follows()
    {
        var config = home.Config with { RemoteLocalRetention = TimeSpan.FromMinutes(1) };
        var shortRetention = new TranscriptTailer(config, store, clock, new AgentLog(config, clock));
        Session("followed", TranscriptTailer.ActiveWindow - TimeSpan.FromHours(1), fileExists: false);
        Session("idle", TranscriptTailer.ActiveWindow + TimeSpan.FromHours(1), fileExists: false);

        shortRetention.RunOnce();

        Assert.Equal(["followed"], Kept());
    }

    [Fact]
    public void The_idle_transcripts_are_looked_at_once_an_hour()
    {
        Session("first", Retention + TimeSpan.FromDays(1), fileExists: false);
        tailer.RunOnce();
        Assert.Empty(Kept());

        Session("second", Retention + TimeSpan.FromDays(1), fileExists: false);
        clock.Advance(TranscriptTailer.PruneEvery - TimeSpan.FromSeconds(1));
        tailer.RunOnce();
        Assert.Equal(["second"], Kept()); // not yet

        clock.Advance(TimeSpan.FromSeconds(2));
        tailer.RunOnce();
        Assert.Empty(Kept());
    }
}
