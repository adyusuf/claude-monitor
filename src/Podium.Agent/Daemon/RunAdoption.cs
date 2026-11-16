using System.Diagnostics;
using Podium.Agent.Exec;

namespace Podium.Agent.Daemon;

/// <summary>
/// What an agent that restarted needs to pick up a run it did not start itself (F-323): whether the stored process is still
/// the one that was started, and a way to stop its process group. A seam so that the restart can be tested without a process.
/// </summary>
public interface IRunAdoption
{
    /// <summary>When the process with this id started; null when no such process exists (it ended).</summary>
    DateTimeOffset? StartOf(int pid);

    /// <summary>Stops the process group led by this id: SIGTERM, or SIGKILL when <paramref name="force"/>. Already gone is not an error.</summary>
    void Signal(int pid, bool force);
}

/// <summary>The operating system's answer: the process table and kill(-pgid). Windows has none of it (the Job Object ends the run with the agent).</summary>
public sealed class SystemRunAdoption : IRunAdoption
{
    public DateTimeOffset? StartOf(int pid)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (ArgumentException)
        {
            return null; // no process has this id
        }
        catch (InvalidOperationException)
        {
            return null; // it ended between the lookup and the read
        }
    }

    public void Signal(int pid, bool force)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A restarted agent does not pick up runs on Windows.");
        UnixRunProcess.SignalGroup(pid, force);
    }
}
