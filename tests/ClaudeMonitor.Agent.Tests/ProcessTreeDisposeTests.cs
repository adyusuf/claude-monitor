using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Disposing a run process releases everything even when the kill-time signal fails (a failing kill must not leak the tracker thread or the pipes).</summary>
public sealed class ProcessTreeDisposeTests
{
    private const string SecretPath = "/home/someone/secret-folder";

    /// <summary>A process table that only counts how often the tracker's thread listed children.</summary>
    private sealed class CountingTable : IProcessTable
    {
        private int listings;
        public int Listings => Volatile.Read(ref listings);

        public ProcessStamp? Find(int pid) => new(pid, 1, pid, 1);

        public IReadOnlyList<ProcessStamp> ChildrenOf(int pid)
        {
            Interlocked.Increment(ref listings);
            return [];
        }
    }

    private sealed class FailingKillProcess(Stream stdout, Stream stderr, DescendantTracker tracker, Exception failure, Action<string>? log)
        : RunProcessBase(stdout, stderr, log)
    {
        public bool Released { get; private set; }

        public void Begin() => BeginWait();

        public override void Kill() => throw failure;

        public override void KillNow() => throw failure;

        protected override RunExit WaitForExit() => new(137, 9);

        protected override void ReleaseNative()
        {
            Released = true;
            tracker.Dispose(); // what the Unix process does: stops the cm-run-track thread
        }
    }

    [Fact]
    public async Task A_kill_that_fails_while_disposing_is_logged_by_type_and_still_releases_the_tracker_and_the_pipes()
    {
        var table = new CountingTable();
        var tracker = new DescendantTracker(100, table, (_, _) => { }, TimeSpan.FromMilliseconds(10));
        var (stdout, stderr) = (new MemoryStream(), new MemoryStream());
        var lines = new List<string>();
        var process = new FailingKillProcess(stdout, stderr, tracker, new IOException($"kill failed at {SecretPath}"), lines.Add);
        process.Begin();
        Assert.True(await ExecHarness.UntilAsync(() => table.Listings > 0), "the tracker thread never polled");

        await process.DisposeAsync(); // does not throw

        Assert.True(process.Released);
        Assert.False(stdout.CanRead);
        Assert.False(stderr.CanRead);
        var line = Assert.Single(lines);
        Assert.Contains(nameof(IOException), line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPath, line, StringComparison.Ordinal);
        var after = table.Listings;
        await Task.Delay(150);
        Assert.Equal(after, table.Listings); // the tracker thread is stopped
    }

    [Fact]
    public async Task A_failure_that_is_not_a_kill_failure_is_logged_by_type_and_does_not_throw_after_releasing_everything()
    {
        var tracker = new DescendantTracker(100, new CountingTable(), (_, _) => { }, TimeSpan.Zero);
        var (stdout, stderr) = (new MemoryStream(), new MemoryStream());
        var lines = new List<string>();
        var process = new FailingKillProcess(stdout, stderr, tracker, new NotSupportedException($"kill broke at {SecretPath}"), lines.Add);
        process.Begin();

        await process.DisposeAsync(); // does not throw

        Assert.True(process.Released);
        Assert.False(stdout.CanRead);
        Assert.False(stderr.CanRead);
        var line = Assert.Single(lines);
        Assert.Contains(nameof(NotSupportedException), line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPath, line, StringComparison.Ordinal);
    }
}
