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

    // The descendants are recorded first (while their parents live), then the group is signalled, then those that left it.
    // Each step runs whatever the others did; the first failure is thrown at the end. The poll may fail with any exception: the
    // run is killed all the same, so none may leave before the group is signalled.
    private void SendToTree(int signal)
    {
        Exception? failure = null;
        Exception? pollFailure = null;
        try
        {
            _tracker?.Poll();
        }
        catch (Exception e)
        {
            failure = pollFailure = e;
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
        catch (IOException e)
        {
            failure ??= e;
        }

        // Logged last: a logger that throws must not keep the group from being signalled. The message may hold a path: only the type.
        if (pollFailure is not null) _log?.Invoke($"descendant poll before kill failed ({pollFailure.GetType().Name})");
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
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
