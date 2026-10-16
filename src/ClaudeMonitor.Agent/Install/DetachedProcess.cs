using System.Diagnostics;

namespace ClaudeMonitor.Agent.Install;

/// <summary>Starts a child with its input and output redirected (and never read), the way the daemon is started: it keeps running after this process ends.</summary>
public sealed class DetachedProcess : IDetachedStarter
{
    public bool Start(string exe, IReadOnlyList<string> args)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var child = Process.Start(info);
            return child is not null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
