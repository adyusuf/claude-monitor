using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The descendant tracker (ADR-0005, macOS): the logic runs on a fake process table everywhere.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class DescendantTrackerTests
{
    private const int Lead = 100;
    private const int Term = 15;
    private static readonly TimeSpan Slow = TimeSpan.FromHours(1);

    /// <summary>A process table the test edits: pid, parent, group and start time per process.</summary>
    private sealed class FakeTable : IProcessTable
    {
        private readonly object gate = new();
        private readonly Dictionary<int, ProcessStamp> procs = [];
        private int listings;
        public Exception? Failure { get; set; }
        public (int Pid, Exception Failure)? FindFailure { get; set; }
        public int Listings => Volatile.Read(ref listings);

        public void Add(int pid, int parent, int group = Lead, long start = 1) { lock (gate) procs[pid] = new ProcessStamp(pid, start, group, parent); }

        public void Remove(int pid) { lock (gate) procs.Remove(pid); }

        public ProcessStamp? Find(int pid)
        {
            if (FindFailure is { } broken && broken.Pid == pid) throw broken.Failure;
            lock (gate) return procs.GetValueOrDefault(pid);
        }

        public IReadOnlyList<ProcessStamp> ChildrenOf(int pid)
        {
            Interlocked.Increment(ref listings);
            if (Failure is not null) throw Failure;
            lock (gate) return [.. procs.Values.Where(p => p.ParentPid == pid)];
        }
    }

    private sealed class Sent
    {
        private readonly List<(int Pid, int Signal)> items = [];
        public List<(int Pid, int Signal)> Items { get { lock (items) return [.. items]; } }
        public void Add(int pid, int signal) { lock (items) items.Add((pid, signal)); }
    }

    private static (DescendantTracker Tracker, FakeTable Table, Sent Sent) Make(TimeSpan? every = null, Action<FakeTable>? seed = null)
    {
        var table = new FakeTable();
        table.Add(Lead, 1);
        seed?.Invoke(table);
        var sent = new Sent();
        return (new DescendantTracker(Lead, table, sent.Add, every ?? Slow), table, sent);
    }

    [Fact]
    public void Every_descendant_of_the_lead_is_recorded_transitively_and_nothing_else()
    {
        var (tracker, table, _) = Make();
        using (tracker)
        {
            table.Add(101, Lead);
            table.Add(102, 101, group: 102); // a grandchild that left the group
            table.Add(103, 102, group: 102);
            table.Add(200, 1); // not ours

            tracker.Poll();

            Assert.Equal([101, 102, 103], tracker.TrackedPids.Order());
        }
    }

    [Fact]
    public void A_descendant_found_once_is_still_signalled_after_its_parent_left_and_it_was_adopted_by_launchd()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead);
            table.Add(102, 101, group: 102);
            tracker.Poll();

            table.Remove(101);
            table.Add(102, 1, group: 102); // re-parented to launchd
            tracker.Poll();
            tracker.Signal(Term);

            Assert.Equal([(102, Term)], sent.Items);
        }
    }

    [Fact]
    public void Only_processes_that_left_the_run_group_are_signalled_so_the_group_signal_is_not_sent_twice()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead); // in the group
            table.Add(102, Lead, group: 102); // setsid
            tracker.Poll();
            tracker.Signal(Term);

            Assert.Equal([(102, Term)], sent.Items);
        }
    }

    [Fact]
    public void A_pid_that_was_reused_by_another_process_is_never_signalled_and_its_children_are_not_followed()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101, start: 10);
            tracker.Poll();

            table.Add(101, 1, group: 101, start: 99); // the old 101 died; an unrelated process got the pid
            table.Add(300, 101, group: 101, start: 5);
            tracker.Signal(Term);
            tracker.Poll();

            Assert.Empty(sent.Items);
            Assert.Empty(tracker.TrackedPids);
        }
    }

    [Fact]
    public void A_lead_pid_that_was_reused_is_not_a_root()
    {
        var table = new FakeTable();
        table.Add(Lead, 1, start: 1);
        using var tracker = new DescendantTracker(Lead, table, (_, _) => { }, Slow);

        table.Add(Lead, 1, start: 2); // the lead was reaped and the pid recycled
        table.Add(400, Lead, group: 400);
        tracker.Poll();

        Assert.Empty(tracker.TrackedPids);
    }

    [Fact]
    public void A_lead_that_was_gone_before_the_tracker_started_has_no_descendants_to_follow()
    {
        var table = new FakeTable();
        table.Add(400, Lead, group: 400);
        using var tracker = new DescendantTracker(Lead, table, (_, _) => { }, Slow);
        tracker.Poll();
        Assert.Empty(tracker.TrackedPids);
    }

    [Fact]
    public void A_failed_signal_does_not_stop_the_others_and_the_first_failure_is_thrown()
    {
        var table = new FakeTable();
        table.Add(Lead, 1);
        table.Add(101, Lead, group: 101);
        table.Add(102, Lead, group: 102);
        table.Add(103, Lead, group: 103);
        var sent = new Sent();
        using var tracker = new DescendantTracker(Lead, table, (pid, sig) =>
        {
            if (pid == 101) throw new IOException("kill failed (errno 1)");
            sent.Add(pid, sig);
        }, Slow);
        tracker.Poll();

        Assert.Throws<IOException>(() => tracker.Signal(Term));
        Assert.Equal([102, 103], sent.Items.Select(i => i.Pid).Order());
    }

    [Fact]
    public void A_descendant_whose_lookup_fails_with_any_exception_does_not_keep_the_others_from_being_signalled()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.Add(102, Lead, group: 102);
            tracker.Poll(); // both recorded, 101 first
            table.FindFailure = (101, new InvalidOperationException("lookup broke"));

            var thrown = Assert.Throws<InvalidOperationException>(() => tracker.Signal(Term)); // the first failure, at the end

            Assert.Equal("lookup broke", thrown.Message);
            Assert.Equal([(102, Term)], sent.Items);
        }
    }

    [Fact]
    public void At_most_the_cap_of_descendants_is_kept()
    {
        var (tracker, table, _) = Make();
        using (tracker)
        {
            for (var pid = 1000; pid < 1000 + DescendantTracker.MaxTracked + 50; pid++) table.Add(pid, Lead, group: pid);
            tracker.Poll();
            Assert.Equal(DescendantTracker.MaxTracked, tracker.TrackedPids.Count);
        }
    }

    [Fact]
    public async Task The_poller_records_in_the_background_and_stops_for_good_when_the_tracker_is_disposed()
    {
        var (tracker, table, _) = Make(TimeSpan.FromMilliseconds(10));
        table.Add(101, Lead, group: 101);
        Assert.True(await ExecHarness.UntilAsync(() => tracker.TrackedPids.Contains(101)), "the background poll never recorded the child");

        tracker.Dispose();
        var after = table.Listings;
        await Task.Delay(150);
        Assert.Equal(after, table.Listings); // no thread is left polling
    }

    [Fact]
    public async Task A_background_poll_that_cannot_read_the_table_is_counted_and_the_kill_time_poll_throws()
    {
        var (tracker, table, _) = Make(TimeSpan.FromMilliseconds(10));
        using (tracker)
        {
            table.Failure = new IOException("proc_listpids failed (errno 1)");
            Assert.True(await ExecHarness.UntilAsync(() => tracker.PollFailures > 0), "the failure was not counted");
            Assert.Throws<IOException>(tracker.Poll);
        }
    }

    [Fact]
    public async Task A_background_poll_that_throws_something_other_than_an_IO_error_does_not_end_the_thread_and_logs_once_per_streak()
    {
        var table = new FakeTable();
        table.Add(Lead, 1);
        var lines = new List<string>();
        using var tracker = new DescendantTracker(Lead, table, (_, _) => { }, TimeSpan.FromMilliseconds(10), line => { lock (lines) lines.Add(line); });
        List<string> Logged() { lock (lines) return [.. lines]; }

        table.Failure = new InvalidOperationException("table broke at /home/someone/secret-folder");
        Assert.True(await ExecHarness.UntilAsync(() => tracker.PollFailures >= 5), "the failures were not counted");
        var first = Assert.Single(Logged()); // five failures, one line
        Assert.Contains(nameof(InvalidOperationException), first, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-folder", first, StringComparison.Ordinal);

        table.Failure = null; // the thread is alive: the next poll records what is there
        table.Add(101, Lead, group: 101);
        Assert.True(await ExecHarness.UntilAsync(() => tracker.TrackedPids.Contains(101)), "the poll thread did not recover");

        table.Failure = new InvalidOperationException("again"); // a new streak logs again
        Assert.True(await ExecHarness.UntilAsync(() => Logged().Count == 2), "the second streak was not logged");
        var failures = tracker.PollFailures;
        Assert.True(await ExecHarness.UntilAsync(() => tracker.PollFailures >= failures + 3));
        Assert.Equal(2, Logged().Count);
    }
}
