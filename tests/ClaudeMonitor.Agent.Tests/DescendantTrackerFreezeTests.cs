using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The freeze phase of the hard kill (ADR-0005, macOS): stop what left the group, poll until nothing new shows up. Fake table, no real process.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class DescendantTrackerFreezeTests
{
    private const int Lead = 100;
    private const int Stop = 17;
    private const int Kill = 9;
    private static readonly TimeSpan Slow = TimeSpan.FromHours(1);

    /// <summary>A process table the test edits; <see cref="AfterLeadListing"/> runs after the lead's n-th listing, so a change shows up from the next poll on.</summary>
    private sealed class Table : IProcessTable
    {
        private readonly object gate = new();
        private readonly Dictionary<int, ProcessStamp> procs = [];
        private int leadListings;
        public Action<int>? AfterLeadListing { get; set; }
        public Exception? Failure { get; set; }

        /// <summary>When set, looking this pid up fails (any exception).</summary>
        public (int Pid, Exception Failure)? FindFailure { get; set; }

        /// <summary>Polls so far: every poll lists the lead's children exactly once.</summary>
        public int Polls => Volatile.Read(ref leadListings);

        public void Add(int pid, int parent, int group = Lead, long start = 1) { lock (gate) procs[pid] = new ProcessStamp(pid, start, group, parent); }

        public ProcessStamp? Find(int pid)
        {
            if (FindFailure is { } broken && broken.Pid == pid) throw broken.Failure;
            lock (gate) return procs.GetValueOrDefault(pid);
        }

        public IReadOnlyList<ProcessStamp> ChildrenOf(int pid)
        {
            if (Failure is not null) throw Failure;
            IReadOnlyList<ProcessStamp> found;
            lock (gate) found = [.. procs.Values.Where(p => p.ParentPid == pid)];
            if (pid == Lead) AfterLeadListing?.Invoke(Interlocked.Increment(ref leadListings));
            return found;
        }
    }

    /// <summary>A clock that only moves when the test says so.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long ticks;

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref ticks);
    }

    private static (DescendantTracker Tracker, Table Table, List<(int Pid, int Signal)> Sent) Make(Action<int, int>? onSignal = null,
        TimeProvider? clock = null, Action<string>? log = null)
    {
        var table = new Table();
        table.Add(Lead, 1);
        var sent = new List<(int Pid, int Signal)>();
        var tracker = new DescendantTracker(Lead, table, (pid, signal) =>
        {
            sent.Add((pid, signal));
            onSignal?.Invoke(pid, signal);
        }, Slow, log, clock);
        return (tracker, table, sent);
    }

    [Fact]
    public void A_child_that_shows_up_only_on_the_second_poll_is_stopped_and_one_more_round_runs_until_the_set_is_stable()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.AfterLeadListing = n => { if (n == 1) table.Add(102, Lead, group: 102); }; // forked after the first poll listed the lead

            tracker.Freeze();

            Assert.Equal([(101, Stop), (102, Stop)], sent);
            Assert.Equal(3, table.Polls); // 101 found; 102 found; nothing new
        }
    }

    [Fact]
    public void A_stable_tree_is_polled_twice_and_each_process_outside_the_group_is_stopped_once_but_the_group_is_left_to_the_caller()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.Add(102, 101, group: 102);
            table.Add(103, Lead); // still in the run's group: the caller's group signal reaches it

            tracker.Freeze();

            Assert.Equal([101, 102], sent.Select(s => s.Pid).Order());
            Assert.All(sent, s => Assert.Equal(Stop, s.Signal));
            Assert.Equal(2, table.Polls); // one round that found them, one that found nothing new
        }
    }

    [Fact]
    public void A_tree_without_descendants_is_polled_once()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            tracker.Freeze();

            Assert.Empty(sent);
            Assert.Equal(1, table.Polls);
        }
    }

    [Theory]
    [InlineData(DescendantTracker.MaxFreezeRounds)]
    [InlineData(3)]
    public void A_tree_that_grows_every_round_is_polled_exactly_the_bound_and_then_left_to_the_kill(int bound)
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.AfterLeadListing = n => table.Add(1000 + n, Lead, group: 1000 + n); // a new child after every poll: every later poll finds one

            if (bound == DescendantTracker.MaxFreezeRounds) tracker.Freeze();
            else tracker.Freeze(bound);

            Assert.Equal(bound, table.Polls);
            Assert.Equal(bound, sent.Count); // 101, then the one new child of every later round
            Assert.All(sent, s => Assert.Equal(Stop, s.Signal));
        }
    }

    [Fact]
    public void A_pid_whose_start_time_changed_after_it_was_recorded_is_not_stopped()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101, start: 10);
            // After the poll recorded 101, the pid belongs to an unrelated process: the signal step looks it up again.
            table.AfterLeadListing = n => { if (n == 1) table.Add(101, 1, group: 101, start: 99); };

            tracker.Freeze();

            Assert.Empty(sent);
        }
    }

    [Fact]
    public void A_failed_stop_does_not_keep_the_others_from_being_stopped_and_the_first_failure_of_any_type_is_thrown_at_the_end()
    {
        var attempts = new List<int>();
        var (tracker, table, sent) = Make((pid, _) =>
        {
            attempts.Add(pid);
            if (pid == 101) throw new InvalidOperationException("stop failed");
        });
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.Add(102, Lead, group: 102);
            table.Add(103, Lead, group: 103);

            var thrown = Assert.Throws<InvalidOperationException>(() => tracker.Freeze());

            Assert.Equal("stop failed", thrown.Message);
            Assert.Equal([101, 102, 103], sent.Select(s => s.Pid).Order()); // all three tried
            Assert.Equal(1, attempts.Count(p => p == 101)); // a failed pid is not retried in the later round
            Assert.Equal(2, table.Polls); // the freeze still ran until the set was stable
        }
    }

    [Fact]
    public void A_poll_that_fails_ends_the_rounds_after_stopping_what_was_recorded_and_the_failure_is_thrown()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            tracker.Poll(); // recorded while the table was readable
            table.Failure = new IOException("table unreadable");

            Assert.Throws<IOException>(() => tracker.Freeze());

            Assert.Equal([(101, Stop)], sent);
        }
    }

    [Fact]
    public void A_descendant_the_freeze_stopped_is_killed_even_when_its_lookup_fails_and_the_lookup_failure_is_reported()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            tracker.Freeze();
            table.FindFailure = (101, new IOException("lookup failed")); // from now on the identity check cannot be made

            Assert.Throws<IOException>(() => tracker.Signal(Kill));

            Assert.Equal([(101, Stop), (101, Kill)], sent); // stopped, then killed: never left stopped for ever
            Assert.Throws<IOException>(() => tracker.Signal(Kill)); // the stopped set was spent: the second kill identity-checks and sends nothing
            Assert.Equal(2, sent.Count);
        }
    }

    [Fact]
    public void A_descendant_the_freeze_did_not_stop_is_still_identity_checked_before_it_is_signalled()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.Add(102, Lead, group: 102);
            tracker.Poll(); // recorded, never frozen
            table.FindFailure = (101, new IOException("lookup failed"));
            table.Add(102, 1, group: 102, start: 99); // 102 is another process now

            Assert.Throws<IOException>(() => tracker.Signal(Kill));

            Assert.Empty(sent); // 101: no lookup, no signal; 102: another process, no signal
        }
    }

    [Fact]
    public void A_descendant_the_freeze_stopped_is_not_killed_when_the_pid_now_positively_belongs_to_another_process()
    {
        var (tracker, table, sent) = Make();
        using (tracker)
        {
            table.Add(101, Lead, group: 101, start: 10);
            tracker.Freeze();
            table.Add(101, 1, group: 101, start: 99); // someone else ended the stopped process and the pid was recycled

            tracker.Signal(Kill);

            Assert.Equal([(101, Stop)], sent);
        }
    }

    [Fact]
    public void A_freeze_past_its_deadline_ends_the_rounds_below_the_bound_and_what_it_stopped_is_still_killed()
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var (tracker, table, sent) = Make(clock: clock, log: lines.Add);
        using (tracker)
        {
            table.Add(101, Lead, group: 101);
            table.AfterLeadListing = n =>
            {
                table.Add(1000 + n, Lead, group: 1000 + n); // the tree keeps growing
                if (n == 2) clock.Advance(DescendantTracker.FreezeDeadline + TimeSpan.FromSeconds(1)); // the second poll was slow
            };

            tracker.Freeze(); // best effort: no exception

            Assert.Equal(2, table.Polls); // far below the bound of 8, the tree still grows
            Assert.Equal([(101, Stop)], sent); // the deadline passed before the processes the second poll found were stopped
            Assert.Contains("deadline", Assert.Single(lines), StringComparison.Ordinal);

            tracker.Signal(Kill);

            Assert.Equal([(101, Stop), (101, Kill), (1001, Kill)], sent); // the stopped one directly, the other after its identity check
        }
    }
}
