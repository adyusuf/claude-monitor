using System.Diagnostics;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;

namespace ClaudeMonitor.Agent.Update;

/// <summary>What the updater needs of the daemon: is it up, stop it, start one from a given binary. A seam: tests never start a real daemon.</summary>
public interface IDaemonControl
{
    bool IsRunning();

    /// <summary>Asks the daemon to stop and waits for it to let go of its lock; false when it did not stop in time.</summary>
    Task<bool> StopAsync(TimeSpan wait, CancellationToken ct);

    bool Start(string binary);
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

    /// <summary>Starts "cm-agent update --auto" detached from this process (the daemon is about to be stopped by it).</summary>
    public static bool SpawnAutoUpdate(AgentConfig config, AgentLog log)
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
            info.ArgumentList.Add("update");
            info.ArgumentList.Add("--auto");
            using var child = Process.Start(info);
            return child is not null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            log.Write($"could not start the update: {e.Message}");
            return false;
        }
    }
}
