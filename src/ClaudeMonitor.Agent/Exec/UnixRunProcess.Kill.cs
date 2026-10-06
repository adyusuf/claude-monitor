using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ClaudeMonitor.Agent.Exec;

// The kill side of a Unix run: the process group, then (macOS) the descendants that left it.
internal sealed unsafe partial class UnixRunProcess
{
    // A run that cannot be tracked is not left running: the process just started is ended and reaped.
    private static DescendantTracker? StartTracker(int pid, TimeSpan every)
    {
        if (every <= TimeSpan.Zero || !OperatingSystem.IsMacOS()) return null;
        try
        {
            return new DescendantTracker(pid, new LibProcTable(), SendToPid, every);
        }
        catch (IOException)
        {
            _ = kill(-pid, Sigkill);
            _ = waitpid(pid, out _, 0);
            throw;
        }
    }

    // The descendants are recorded first (while their parents live), then the group is signalled, then those that left it.
    // Each step runs whatever the others did; the first failure is thrown at the end.
    private void SendToTree(int signal)
    {
        IOException? failure = null;
        try
        {
            _tracker?.Poll();
        }
        catch (IOException e)
        {
            failure = e;
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
