using System.Runtime.ExceptionServices;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>A process as the table sees it: pid plus start time (microseconds since the epoch) identify it for good.</summary>
internal sealed record ProcessStamp(int Pid, long StartMicros, int GroupId, int ParentPid);

/// <summary>The process table: one process by pid, and the live children of one pid.</summary>
internal interface IProcessTable
{
    /// <summary>Null when the process is gone (or not visible to this account).</summary>
    ProcessStamp? Find(int pid);

    IReadOnlyList<ProcessStamp> ChildrenOf(int pid);
}

/// <summary>
/// macOS has no cgroup, so a process that leaves the run's process group (setsid) would survive a kill. This tracker records,
/// while the run lives, every descendant of the lead by following parent pids from the lead and from what it already
/// recorded, polling every <c>every</c> and once more right before a kill. Kill and KillNow then also signal every recorded
/// descendant that is still the same process (same pid AND start time) and no longer in the run's group.
/// BEST EFFORT: a process that forks and re-parents to launchd (double fork, or its parent dies) between two polls is never
/// seen. The graceful stop only narrows the fork window with a poll right before it; the hard kill closes it with
/// <see cref="Freeze"/> (stop everything, poll again until nothing new shows up).
/// </summary>
internal sealed partial class DescendantTracker : IDisposable
{
    /// <summary>Descendants kept at most; a fork bomb cannot make the tracker grow without bound.</summary>
    public const int MaxTracked = 4096;
    private static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(5);

    private readonly IProcessTable _table;
    private readonly Action<int, int> _signal;
    private readonly int _lead;
    private readonly ProcessStamp? _leadStamp;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ProcessStamp> _tracked = [];
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread? _thread;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly HashSet<(int Pid, long StartMicros)> _frozen = []; // what Freeze stopped and the kill step has not signalled yet
    private int _pollFailures;

    /// <summary>
    /// <paramref name="lead"/> is the run's lead pid and group id; <paramref name="signal"/> sends (pid, signal) and throws an
    /// IOException for any failure but "no such process". <paramref name="every"/> zero or less: no background polling.
    /// <paramref name="log"/> takes one line (an error type name, no content) the first time a background poll fails after one
    /// that worked. <paramref name="time"/> (tests) is the clock of the freeze deadline.
    /// </summary>
    public DescendantTracker(int lead, IProcessTable table, Action<int, int> signal, TimeSpan every, Action<string>? log = null, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
        _lead = lead;
        _table = table;
        _signal = signal;
        _leadStamp = table.Find(lead);
        if (every <= TimeSpan.Zero) return;
        _thread = new Thread(() => PollLoop(every)) { IsBackground = true, Name = "cm-run-track" };
        _thread.Start();
    }

    /// <summary>Background polls that failed; the kill-time poll reports a failure that lasts.</summary>
    public int PollFailures => Volatile.Read(ref _pollFailures);

    public IReadOnlyCollection<int> TrackedPids
    {
        get
        {
            lock (_gate) return [.. _tracked.Keys];
        }
    }

    /// <summary>Records the descendants alive now. Throws an IOException when the process table cannot be read.</summary>
    public void Poll()
    {
        lock (_gate)
        {
            var queue = new Queue<int>();
            if (_leadStamp is not null && IsSame(_leadStamp)) queue.Enqueue(_lead);
            foreach (var recorded in _tracked.Values.ToList())
            {
                if (IsSame(recorded)) queue.Enqueue(recorded.Pid);
                else _tracked.Remove(recorded.Pid); // gone, or the pid belongs to someone else now
            }

            while (queue.Count > 0)
            {
                foreach (var child in _table.ChildrenOf(queue.Dequeue()))
                {
                    if (child.Pid == _lead || _tracked.ContainsKey(child.Pid) || _tracked.Count >= MaxTracked) continue;
                    _tracked[child.Pid] = child;
                    queue.Enqueue(child.Pid);
                }
            }
        }
    }

    /// <summary>
    /// Sends the signal to every descendant the freeze stopped, then to every other recorded descendant that is the same process
    /// as when it was recorded and has left the run's group (the group signal reaches the rest, once). Continues past a
    /// failure and throws the first one at the end.
    /// A descendant the freeze stopped is signalled whatever its lookup does: a STOPPED process cannot exit by itself, so its
    /// pid cannot have been recycled, and a failing lookup must never leave it stopped for ever. The lookup is still made to
    /// refuse a pid that now positively belongs to another process (someone else ended the stopped one, in the meantime).
    /// The stopped set is spent by this call: a second kill (the disposal after a hard kill) identity-checks like any other.
    /// </summary>
    public void Signal(int signal)
    {
        List<ProcessStamp> targets;
        List<(int Pid, long StartMicros)> stopped;
        lock (_gate)
        {
            targets = [.. _tracked.Values];
            stopped = [.. _frozen];
            _frozen.Clear();
        }

        Exception? failure = null;
        foreach (var (pid, start) in stopped)
        {
            var other = false;
            try
            {
                other = _table.Find(pid) is { } now && now.StartMicros != start;
            }
            catch (Exception e)
            {
                failure ??= e; // any failure: the stopped process is signalled all the same
            }

            try
            {
                if (!other) _signal(pid, signal);
            }
            catch (Exception e)
            {
                failure ??= e;
            }
        }

        var spent = stopped.ToHashSet();
        foreach (var recorded in targets.Where(t => !spent.Contains((t.Pid, t.StartMicros))))
        {
            try
            {
                if (_table.Find(recorded.Pid) is { } now && now.StartMicros == recorded.StartMicros && now.GroupId != _lead) _signal(recorded.Pid, signal);
            }
            catch (Exception e)
            {
                failure ??= e; // any failure: the remaining descendants are still signalled
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    public void Dispose()
    {
        _stop.Set();
        // A poll that outlasts the wait ends the thread at its next check (it is a background thread); the event is then left to the GC.
        if (_thread is null || _thread.Join(JoinWait)) _stop.Dispose();
    }

    private bool IsSame(ProcessStamp recorded) => _table.Find(recorded.Pid) is { } now && now.StartMicros == recorded.StartMicros;

    // Nothing may leave this thread: an exception here would end the daemon in the middle of a run.
    private void PollLoop(TimeSpan every)
    {
        var failing = false;
        while (!_stop.Wait(every))
        {
            try
            {
                Poll();
                failing = false;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _pollFailures); // counted; the kill-time Poll throws if the table is still unreadable
                if (!failing) _log?.Invoke($"descendant poll failed ({e.GetType().Name})"); // once per streak
                failing = true;
            }
        }
    }
}
