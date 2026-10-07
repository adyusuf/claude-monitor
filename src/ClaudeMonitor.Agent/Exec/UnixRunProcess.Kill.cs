using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ClaudeMonitor.Agent.Exec;

// The kill side of a Unix run: the process group, then (macOS) the descendants that left it.
internal sealed unsafe partial class UnixRunProcess
{
    // A run that cannot be tracked is not left running: the process just started is ended and reaped.
    private static DescendantTracker? StartTracker(int pid, TimeSpan every, Action<string>? log)
    {
        if (every <= TimeSpan.Zero || !OperatingSystem.IsMacOS()) return null;
        try
        {
            return new DescendantTracker(pid, new LibProcTable(), SendToPid, every, log);
        }
        catch (IOException)
        {
            _ = kill(-pid, Sigkill);
            _ = waitpid(pid, out _, 0);
            throw;
        }
    }

    // The hard kill (freeze true) first stops the whole tree (macOS, with a tracker): nothing can fork any more, and the tracker
    // polls until the set of descendants is stable. The graceful stop never freezes: a process has to run to handle SIGTERM.
    // Then the descendants are recorded (while their parents live), the group is signalled, then those that left it.
    // Each step runs whatever the others did; the first failure is thrown at the end. The freeze and the poll may fail with
    // any exception: the run is killed all the same, so none may leave before the group is signalled. SIGKILL needs no SIGCONT.
    private void SendToTree(int signal, bool freeze = false)
    {
        Exception? failure = null;
        Exception? freezeFailure = null;
        Exception? pollFailure = null;
        if (freeze && _tracker is not null)
        {
            try
            {
                SendToGroup(DescendantTracker.StopSignal);
            }
            catch (Exception e)
            {
                failure ??= e; // any failure: the kill below is sent whatever the freeze did
            }

            try
            {
                _tracker.Freeze();
            }
            catch (Exception e)
            {
                freezeFailure = e; // two statements: an earlier failure in failure must not keep this one from being logged
                failure ??= e;
            }
        }

        try
        {
            _tracker?.Poll();
        }
        catch (Exception e)
        {
            failure ??= e; // the first failure stays the one that is thrown
            pollFailure = e;
        }

        try
        {
            SendToGroup(signal);
        }
        catch (IOException e)
        {
            failure ??= e;
        }

        try
        {
            _tracker?.Signal(signal);
        }
        catch (Exception e)
        {
            failure ??= e;
        }

        // Logged last, at most one failure line per kill (the freeze first: it reads the table before the poll does; the deadline
        // line is written by the freeze itself):
        // a logger that throws must not keep the group from being signalled. The message may hold a path: only the type.
        try
        {
            if (freezeFailure is not null) _log?.Invoke($"descendant freeze before kill failed ({freezeFailure.GetType().Name})");
            else if (pollFailure is not null) _log?.Invoke($"descendant poll before kill failed ({pollFailure.GetType().Name})");
        }
        finally
        {
            // The real failure reaches the caller even if the logger threw: a throw in a finally replaces the logger's exception,
            // which cannot be logged anywhere (it is the logger that failed).
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static void SendToPid(int pid, int signal)
    {
        if (kill(pid, signal) == 0) return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno != Esrch) throw new IOException($"kill failed (errno {errno})"); // ESRCH: already gone
    }

    private void SendToGroup(int signal)
    {
        if (kill(-_pid, signal) == 0) return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno != Esrch) throw new IOException($"kill failed (errno {errno})"); // ESRCH: the group is already gone
    }
}
