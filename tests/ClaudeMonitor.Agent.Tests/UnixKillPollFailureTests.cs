using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A kill whose descendant poll fails with any exception still signals the group and the recorded descendants.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixKillPollFailureTests
{
    private const int Term = 15;
    private const int Kill = 9;
    private const int Stop = 17; // the tracker's SIGSTOP (macOS); on Linux the same number is SIGCHLD, which the sleeping lead ignores, so these run there too
    private const int Child = 4242;
    private const string SecretPath = "/home/someone/secret-folder";
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };

    /// <summary>A table with the lead and one descendant outside its group; listing the children can be made to fail.</summary>
    private sealed class Table(int lead) : IProcessTable
    {
        public Exception? Failure { get; set; }

        /// <summary>When set, looking the LEAD up fails: the poll starts with the lead, so this is how a poll fails before it lists any child.</summary>
        public Exception? LeadFindFailure { get; set; }

        /// <summary>
        /// When set, looking the DESCENDANT up fails: the signal step of a kill looks each recorded descendant up again. (A poll also
        /// looks recorded descendants up, so a test that wants the poll to fail with another exception fails the lead's lookup.)
        /// </summary>
        public Exception? FindFailure { get; set; }

        public ProcessStamp? Find(int pid)
        {
            if (pid == lead && LeadFindFailure is not null) throw LeadFindFailure;
            if (pid == Child && FindFailure is not null) throw FindFailure;
            return pid == lead ? new ProcessStamp(pid, 1, pid, 1) : pid == Child ? new ProcessStamp(Child, 1, Child, lead) : null;
        }

        public IReadOnlyList<ProcessStamp> ChildrenOf(int pid)
        {
            if (Failure is not null) throw Failure;
            return pid == lead ? [new ProcessStamp(Child, 1, Child, lead)] : [];
        }
    }

    [Fact]
    public async Task A_poll_that_fails_at_kill_time_with_any_exception_still_ends_the_group_and_signals_the_descendants()
    {
        if (!ExecFixture.Unix) return;
        var sent = new List<(int Pid, int Signal)>();
        var lines = new List<string>();
        Table? table = null;
        DescendantTracker? tracker = null;
        await using var run = UnixRunProcess.Start("/bin/sleep", ["sleep", "300"], Path.GetTempPath(), Env, log: lines.Add, trackerFor: pid =>
        {
            table = new Table(pid);
            return tracker = new DescendantTracker(pid, table, (target, signal) => { lock (sent) sent.Add((target, signal)); }, TimeSpan.Zero);
        });
        tracker!.Poll(); // the descendant is recorded while the table is readable
        table!.Failure = new InvalidOperationException($"table broke at {SecretPath}");

        var thrown = Assert.Throws<InvalidOperationException>(run.Kill); // the first failure is reported after the signals went out

        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30)); // the group signal was sent: the lead is gone
        Assert.Equal(Term, exit.Signal); // ended by the group's SIGTERM, not by itself
        Assert.Equal([(Child, Term)], sent);
        var line = Assert.Single(lines);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPath, line, StringComparison.Ordinal);
        Assert.Contains(SecretPath, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_log_that_throws_neither_keeps_the_group_from_being_signalled_nor_hides_the_failure()
    {
        if (!ExecFixture.Unix) return;
        var sent = new List<(int Pid, int Signal)>();
        Table? table = null;
        DescendantTracker? tracker = null;
        var broken = true; // the log fails once (at the kill), then works again so the disposal's own line is written
        await using var run = UnixRunProcess.Start("/bin/sleep", ["sleep", "300"], Path.GetTempPath(), Env,
            log: _ =>
            {
                if (!broken) return;
                broken = false;
                throw new InvalidOperationException("the log is broken");
            }, trackerFor: pid =>
        {
            table = new Table(pid);
            return tracker = new DescendantTracker(pid, table, (target, signal) => { lock (sent) sent.Add((target, signal)); }, TimeSpan.Zero);
        });
        tracker!.Poll();
        table!.Failure = new InvalidOperationException("table broke");

        // The table's failure is the one reported, after the signals, not the log's: the message tells them apart.
        var thrown = Assert.Throws<InvalidOperationException>(run.Kill);
        Assert.Equal("table broke", thrown.Message);

        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Term, exit.Signal); // the group was signalled before the log was written
        Assert.Equal([(Child, Term)], sent);
    }

    [Fact]
    public async Task A_signal_step_that_fails_with_any_exception_neither_skips_the_poll_failure_log_nor_replaces_the_first_failure()
    {
        if (!ExecFixture.Unix) return;
        var lines = new List<string>();
        Table? table = null;
        DescendantTracker? tracker = null;
        await using var run = UnixRunProcess.Start("/bin/sleep", ["sleep", "300"], Path.GetTempPath(), Env, log: lines.Add, trackerFor: pid =>
        {
            table = new Table(pid);
            return tracker = new DescendantTracker(pid, table, (_, _) => { }, TimeSpan.Zero);
        });
        tracker!.Poll();
        table!.LeadFindFailure = new InvalidOperationException("table broke"); // the poll at kill time fails (it starts with the lead) ...
        table.FindFailure = new NotSupportedException("find broke"); // ... and so does the signal step (it looks the descendant up), with another type

        var thrown = Assert.Throws<InvalidOperationException>(run.Kill);

        Assert.Equal("table broke", thrown.Message); // the FIRST failure, not the later one
        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Term, exit.Signal); // the group was signalled whatever failed
        var line = Assert.Single(lines); // the poll failure was logged (by type name) although the signal step failed after it
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hard_kill_stops_a_descendant_before_it_kills_it()
    {
        if (!ExecFixture.Unix) return;
        var sent = new List<(int Pid, int Signal)>();
        DescendantTracker? tracker = null;
        await using var run = UnixRunProcess.Start("/bin/sleep", ["sleep", "300"], Path.GetTempPath(), Env, trackerFor: pid =>
            tracker = new DescendantTracker(pid, new Table(pid), (target, signal) => { lock (sent) sent.Add((target, signal)); }, TimeSpan.Zero));
        tracker!.Poll();

        run.KillNow();

        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Kill, exit.Signal); // a stopped group is still ended by SIGKILL, no SIGCONT needed
        Assert.Equal([(Child, Stop), (Child, Kill)], sent); // stopped first, then killed
    }

    [Fact]
    public async Task A_freeze_that_fails_with_any_exception_does_not_keep_the_kill_from_being_sent_and_its_failure_reaches_the_caller()
    {
        if (!ExecFixture.Unix) return;
        var sent = new List<(int Pid, int Signal)>();
        var lines = new List<string>();
        DescendantTracker? tracker = null;
        await using var run = UnixRunProcess.Start("/bin/sleep", ["sleep", "300"], Path.GetTempPath(), Env, log: lines.Add, trackerFor: pid =>
            tracker = new DescendantTracker(pid, new Table(pid), (target, signal) =>
            {
                lock (sent) sent.Add((target, signal));
                if (signal == Stop) throw new InvalidOperationException($"stop broke at {SecretPath}");
            }, TimeSpan.Zero));
        tracker!.Poll();

        var thrown = Assert.Throws<InvalidOperationException>(run.KillNow);

        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Kill, exit.Signal); // the group was killed although the freeze failed
        Assert.Equal([(Child, Stop), (Child, Kill)], sent); // and so was the descendant
        Assert.Contains(SecretPath, thrown.Message, StringComparison.Ordinal);
        var line = Assert.Single(lines);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPath, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_freeze_and_then_the_kill_time_poll_both_fail_the_caller_gets_the_freeze_failure_and_the_kill_is_still_sent()
    {
        if (!ExecFixture.Unix) return;
        var sent = new List<(int Pid, int Signal)>();
        var lines = new List<string>();
        Table? table = null;
        DescendantTracker? tracker = null;
        await using var run = UnixRunProcess.Start("/bin/sleep", ["sleep", "300"], Path.GetTempPath(), Env, log: lines.Add, trackerFor: pid =>
        {
            table = new Table(pid);
            return tracker = new DescendantTracker(pid, table, (target, signal) =>
            {
                lock (sent) sent.Add((target, signal));
                if (signal != Stop) return;
                table.Failure = new IOException("table broke"); // the freeze's next poll and the kill-time poll fail ...
                throw new InvalidOperationException("stop broke"); // ... after this, the FIRST failure
            }, TimeSpan.Zero);
        });
        tracker!.Poll();

        var thrown = Assert.Throws<InvalidOperationException>(run.KillNow);

        Assert.Equal("stop broke", thrown.Message); // not the later poll's IOException
        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Kill, exit.Signal);
        Assert.Equal([(Child, Stop), (Child, Kill)], sent);
        var line = Assert.Single(lines); // one line per kill: the freeze's
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
    }
}
