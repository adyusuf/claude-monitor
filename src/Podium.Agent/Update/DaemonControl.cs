using System.Diagnostics;
using Podium.Agent.Config;
using Podium.Agent.Daemon;

namespace Podium.Agent.Update;

/// <summary>What the updater needs of the daemon: is it up, stop it, start one from a given binary. A seam: tests never start a real daemon.</summary>
public interface IDaemonControl
{
    bool IsRunning();

    /// <summary>Asks the daemon to stop and waits for it to let go of its lock; false when it did not stop in time.</summary>
    Task<bool> StopAsync(TimeSpan wait, CancellationToken ct);

    bool Start(string binary);

    /// <summary>Ends a daemon that ignores a stop request (a hung one); true when none holds the lock afterwards.</summary>
    bool Kill();
}

/// <summary>
/// The real thing. There is no service manager behind the agent (hooks start the daemon on demand), so a stop is a request
/// file the daemon watches for, and "stopped" is the daemon's exclusive lock becoming free.
/// </summary>
public sealed class DaemonControl(AgentConfig config, AgentLog log, TimeProvider clock) : IDaemonControl
{
    public bool IsRunning()
    {
        config.EnsureHome();
        using var probe = DaemonHost.TryLock(config.LockPath);
        return probe is null;
    }

    public async Task<bool> StopAsync(TimeSpan wait, CancellationToken ct)
    {
        if (!IsRunning()) return true;
        File.WriteAllText(config.StopRequestPath, clock.GetUtcNow().ToString("O"));
        var until = clock.GetUtcNow() + wait;
        while (clock.GetUtcNow() < until)
        {
            if (!IsRunning()) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct);
        }

        try
        {
            File.Delete(config.StopRequestPath); // never leave a request behind for the next daemon to obey
        }
        catch (IOException)
        {
            // best effort
        }

        return !IsRunning();
    }

    public bool Start(string binary) => DaemonHost.EnsureRunning(config, log, binary);

    /// <summary>
    /// Only the process the daemon wrote into daemon.pid, only while the lock is held, and only when it is still a aipod
    /// (a recycled process id must never take an unrelated program with it).
    /// </summary>
    public bool Kill()
    {
        if (!IsRunning()) return true;
        try
        {
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(config.PidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture));
            if (!Config.Brand.IsOurProcess(process.ProcessName)) return false;
            log.Write($"killing the daemon (pid {process.Id}): it did not stop when asked");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(config.UpdateStopWait);
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }

        return !IsRunning();
    }

    /// <summary>Starts "aipod update --auto" detached from this process (the daemon is about to be stopped by it).</summary>
    public static bool SpawnAutoUpdate(AgentConfig config, AgentLog log) => Spawn(config, log, UpdateCommand.Verb, UpdateCommand.AutoFlag);

    /// <summary>Starts "aipod &lt;args&gt;" detached from this process: an action asked for on the web (restart, update) ends this daemon and starts the next.</summary>
    public static bool Spawn(AgentConfig config, AgentLog log, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(config.BinaryPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var child = Process.Start(info);
            return child is not null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            log.Write($"could not start {(args.FirstOrDefault() == UpdateCommand.Verb ? "the update" : $"aipod {args.FirstOrDefault()}")}: {e.Message}");
            return false;
        }
    }
}
