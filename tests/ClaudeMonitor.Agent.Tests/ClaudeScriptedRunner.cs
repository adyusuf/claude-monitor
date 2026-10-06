using System.Collections.Concurrent;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// A fake that implements the cancellable, outcome-telling overload of the process runner (the one the Claude updater uses) and
/// lets a test decide how `claude update` ends. Like the real runner it starts nothing once the token is cancelled. The plain
/// overload throws: the updater must not use it, or it could not be stopped.
/// </summary>
public sealed class ScriptedClaudeRunner : IProcessRunner
{
    public const string VersionText = "2.1.285 (Claude Code)";

    public ConcurrentQueue<string> Asked { get; } = new();
    public ConcurrentQueue<(string Args, TimeSpan Timeout, CancellationToken Token)> Calls { get; } = new();

    /// <summary>How `claude update` ends; gets the token the updater passed (a test can block on it).</summary>
    public Func<CancellationToken, ProcessResult> OnUpdate { get; set; } = _ => new(ProcessEnd.Exited, 0, "");

    /// <summary>Runs inside every `--version` call (a test can cancel the daemon's token there).</summary>
    public Action? OnVersion { get; set; }

    /// <summary>What `--version` prints (a test can change it between attempts, as an update would).</summary>
    public string Version { get; set; } = VersionText;

    public int Updates => Asked.Count(a => a == "update");

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout) =>
        throw new NotSupportedException("the updater must use the overload that takes a cancellation token");

    public ProcessResult Run(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var line = string.Join(' ', args);
        Asked.Enqueue(line);
        Calls.Enqueue((line, timeout, ct));
        if (ct.IsCancellationRequested) return new(ProcessEnd.Cancelled, -1, "");
        if (line == "--version")
        {
            OnVersion?.Invoke();
            return new(ProcessEnd.Exited, 0, Version);
        }

        return line == "update" ? OnUpdate(ct) : new(ProcessEnd.Exited, -1, "");
    }
}
