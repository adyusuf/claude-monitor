using System.Diagnostics;

namespace ClaudeMonitor.Agent.Update;

/// <summary>Runs a program and returns what it printed; a seam so the updater is tested without running anything real.</summary>
public interface IProcessRunner
{
    /// <summary>The exit code (-1 when it could not be started or did not end in time) and its standard output.</summary>
    (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(args);
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, "");
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "");
        }
    }
}
