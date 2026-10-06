using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A kill whose descendant poll fails with any exception still signals the group and the recorded descendants.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixKillPollFailureTests
{
    private const int Term = 15;
    private const int Child = 4242;
    private const string SecretPath = "/home/someone/secret-folder";
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };

    /// <summary>A table with the lead and one descendant outside its group; listing the children can be made to fail.</summary>
    private sealed class Table(int lead) : IProcessTable
    {
        public Exception? Failure { get; set; }

        public ProcessStamp? Find(int pid) => pid == lead ? new ProcessStamp(pid, 1, pid, 1) : pid == Child ? new ProcessStamp(Child, 1, Child, lead) : null;

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
    public async Task A_log_that_throws_cannot_keep_the_group_from_being_signalled()
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

        Assert.Throws<InvalidOperationException>(run.Kill); // the log's own failure replaces the report, after the signals

        var exit = await run.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Term, exit.Signal); // the group was signalled before the log was written
        Assert.Equal([(Child, Term)], sent);
    }
}
