using System.Runtime.ExceptionServices;

namespace ClaudeMonitor.Agent.Exec;

// The freeze phase of the hard kill (ADR-0005): nothing can fork any more once it is stopped.
internal sealed partial class DescendantTracker
{
    /// <summary>SIGSTOP on macOS (the only platform with a tracker; Linux numbers it 19, so this is never to be used there).</summary>
    public const int StopSignal = 17;

    /// <summary>Polls a freeze makes at most: a tree that keeps growing past it is left to the kill that follows.</summary>
    public const int MaxFreezeRounds = 8;

    /// <summary>Wall-clock time a freeze may take: the tree is STOPPED meanwhile, and one poll walks the whole process table.</summary>
    public static readonly TimeSpan FreezeDeadline = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Stops every recorded descendant that left the run's group (the caller stops the group itself with one group signal), so
    /// that nothing can fork, then polls again to find the children that were forked before the stop (through their parent's
    /// pid) and stops them too, until a poll finds no pid that the previous one did not know. BEST EFFORT: a tree that still
    /// grows after <paramref name="maxRounds"/> polls, or when <see cref="FreezeDeadline"/> has passed (checked before each round
    /// and each stop; a poll of a small tree takes a few milliseconds, a runaway one seconds), is left as it is, and the caller
    /// kills it all the same: what was stopped so far is remembered for <see cref="Signal"/>. A pid is stopped only while it is the same process (pid AND start time), as <see cref="Signal"/> does.
    /// Goes on past a failure (any exception, also from the poll, which then ends the rounds) and throws the first one at
    /// the end: the caller has to send the kill whatever this did, since SIGKILL works on a stopped process.
    /// </summary>
    public void Freeze(int maxRounds = MaxFreezeRounds)
    {
        var started = _time.GetTimestamp();
        var expired = false;
        Exception? failure = null;
        HashSet<int> known = [];
        HashSet<(int Pid, long StartMicros)> attempted = [];
        for (var round = 0; round < maxRounds; round++)
        {
            if (_time.GetElapsedTime(started) >= FreezeDeadline)
            {
                expired = true;
                break;
            }

            var pollFailed = false;
            try
            {
                Poll();
            }
            catch (Exception e)
            {
                failure ??= e;
                pollFailed = true; // what is recorded already is still stopped below, then the rounds end
            }

            List<ProcessStamp> targets;
            lock (_gate) targets = [.. _tracked.Values];
            var grew = targets.Exists(t => !known.Contains(t.Pid));
            known = [.. targets.Select(t => t.Pid)];
            foreach (var recorded in targets)
            {
                if (_time.GetElapsedTime(started) >= FreezeDeadline)
                {
                    expired = true;
                    break;
                }

                try
                {
                    if (_table.Find(recorded.Pid) is { } now && now.StartMicros == recorded.StartMicros && now.GroupId != _lead
                        && attempted.Add((now.Pid, now.StartMicros)))
                    {
                        _signal(recorded.Pid, StopSignal);
                        lock (_gate) _frozen.Add((now.Pid, now.StartMicros)); // only after it was stopped: Signal kills these without a lookup
                    }
                }
                catch (Exception e)
                {
                    failure ??= e; // any failure: the remaining descendants are still stopped
                }
            }

            if (pollFailed || expired || !grew) break;
        }

        if (expired)
        {
            try
            {
                _log?.Invoke("descendant freeze reached its deadline"); // best effort: not an error, the kill follows
            }
            catch (Exception e)
            {
                failure ??= e;
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }
}
