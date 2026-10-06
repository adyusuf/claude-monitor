using System.Diagnostics;

namespace ClaudeMonitor.Agent.Update;

/// <summary>How a run ended: it exited on its own, it could not be started, or it was killed (time ran out, or the caller cancelled).</summary>
public enum ProcessEnd { Exited, NotStarted, TimedOut, Cancelled }

/// <summary>What a run ended with; <paramref name="Error"/> is an exception type name only (a failed start or kill), never output.</summary>
public sealed record ProcessResult(ProcessEnd End, int ExitCode, string Output, string? Error = null);

/// <summary>Runs a program and returns what it printed; a seam so the updater is tested without running anything real.</summary>
public interface IProcessRunner
{
    /// <summary>The exit code (-1 when it could not be started or did not end in time) and its standard output.</summary>
    (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout);

    /// <summary>
    /// As <see cref="Run(string, IReadOnlyList{string}, TimeSpan)"/>, but cancellable and telling how it ended: on cancellation the
    /// process tree is killed. A runner that cannot cancel (the test fakes) inherits this: it never starts once cancelled, and reports an exit.
    /// </summary>
    ProcessResult Run(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new(ProcessEnd.Cancelled, -1, "");
        var (exit, output) = Run(file, args, timeout);
        return new(ProcessEnd.Exited, exit, output);
    }
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var result = Run(file, args, timeout, CancellationToken.None);
        return (result.End == ProcessEnd.Exited ? result.ExitCode : -1, result.Output);
    }

    public ProcessResult Run(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ct.IsCancellationRequested) return new(ProcessEnd.Cancelled, -1, "");
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        Process? started;
        try
        {
            started = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(ProcessEnd.NotStarted, -1, "", e.GetType().Name);
        }

        if (started is null) return new(ProcessEnd.NotStarted, -1, "");
        using var process = started;
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            process.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            return new(ct.IsCancellationRequested ? ProcessEnd.Cancelled : ProcessEnd.TimedOut, -1, "", Kill(process));
        }
        catch (OperationCanceledException)
        {
            // it ended on its own just as the wait was given up: an exit like any other
        }

        try
        {
            return new(ProcessEnd.Exited, process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            return new(ProcessEnd.Exited, process.ExitCode, "", e.GetType().Name);
        }
    }

    /// <summary>Ends the whole tree (an install may have started children); null when it worked, else the error's type name.</summary>
    private static string? Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
        {
            return e.GetType().Name;
        }
    }
}
