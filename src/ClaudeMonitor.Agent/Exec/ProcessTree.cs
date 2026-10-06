using System.ComponentModel;
using System.Text;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>How a run's lead process ended: its exit code, or the signal that killed it (Code is then 128 + Signal).</summary>
public sealed record RunExit(int Code, int? Signal);

/// <summary>
/// A started run whose WHOLE process tree can be killed (ADR-0005, "The executor on the target"). Stdout and Stderr are the
/// read ends of the child's pipes; its stdin is the null device and nothing else is inherited.
/// </summary>
public interface IRunProcess : IAsyncDisposable
{
    Stream Stdout { get; }
    Stream Stderr { get; }

    /// <summary>Completes when the lead process is reaped. It faults only when the OS cannot report the exit.</summary>
    Task<RunExit> Exited { get; }

    /// <summary>
    /// The polite stop of the whole tree: SIGTERM to the process group on Unix. Windows has no such signal for a process
    /// without a console window, so there it ends the whole Job Object at once, exactly like <see cref="KillNow"/>.
    /// The caller waits its grace period and then calls <see cref="KillNow"/>, whether or not the lead process left:
    /// its children may have ignored the signal.
    /// </summary>
    void Kill();

    /// <summary>SIGKILL to the process group (Unix) or TerminateJobObject (Windows). Safe to call again and after the exit.</summary>
    void KillNow();
}

/// <summary>Starts a run so that its descendants can be found again: an own process group (Unix) or a Job Object (Windows).</summary>
public static class ProcessTree
{
    /// <summary>
    /// Starts exe with args (argv mode). The environment is exactly <paramref name="env"/>, never inherited. argv0 is what
    /// the child sees as its own name (default: exe). Throws when the OS refuses to start it; the message carries no argument.
    /// <paramref name="limits"/> are applied on macOS through <see cref="RunLauncher"/> (and nowhere else, see <see cref="RunLimits"/>).
    /// <paramref name="trackEvery"/> above zero makes a kill reach descendants that left the process group, on macOS only
    /// (<see cref="DescendantTracker"/>, best effort).
    /// </summary>
    public static IRunProcess Start(string exe, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> env,
        string? argv0 = null, RunLimits? limits = null, TimeSpan trackEvery = default, Action<string>? log = null)
    {
        Validate(exe, args, cwd, env);
        if (OperatingSystem.IsWindows())
        {
            if (exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                // CreateProcess hands a batch file to cmd.exe, which re-parses the arguments: argv mode would become shell mode.
                throw new ArgumentException("A batch file cannot run in argv mode.", nameof(exe));
            }

            var line = new StringBuilder(QuoteWindowsArg(argv0 ?? exe));
            foreach (var a in args) line.Append(' ').Append(QuoteWindowsArg(a));
            return WindowsRunProcess.Start(exe, line.ToString(), cwd, env, log);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            IReadOnlyList<string> argv = [argv0 ?? exe, .. args];
            if (limits is null || !RunLimits.AppliesHere) return UnixRunProcess.Start(exe, argv, cwd, env, trackEvery, log);
            UnixRunProcess.RequireExecutable(exe);
            var (launcher, wrapped) = RunLauncher.Wrap(exe, argv, limits);
            return UnixRunProcess.Start(launcher, wrapped, cwd, env, trackEvery, log);
        }

        throw new PlatformNotSupportedException("Remote runs are supported on Windows, macOS and Linux.");
    }

    /// <summary>
    /// Starts a shell run: /bin/sh -c text on Unix, cmd.exe /d /s /c "text" on Windows. <paramref name="shell"/> is the
    /// shell's path as the guard resolved it (default: /bin/sh, or cmd.exe in the system folder).
    /// </summary>
    public static IRunProcess StartShell(string text, string cwd, IReadOnlyDictionary<string, string> env, string? shell = null,
        RunLimits? limits = null, TimeSpan trackEvery = default, Action<string>? log = null)
    {
        if (OperatingSystem.IsWindows())
        {
            Validate(text, [], cwd, env);
            var cmd = shell ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return WindowsRunProcess.Start(cmd, $"\"{cmd}\" /d /s /c \"{text}\"", cwd, env, log);
        }

        return Start(shell ?? UnixShell, ["-c", text], cwd, env, argv0: "sh", limits, trackEvery, log);
    }

    private const string UnixShell = "/bin/sh";

    /// <summary>One argument as the Microsoft C runtime (and CommandLineToArgvW) reads it back unchanged.</summary>
    public static string QuoteWindowsArg(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // backslashes only count double in front of a quote (or the closing one)
            if (c == '"') sb.Append('\\', (backslashes * 2) + 1);
            else sb.Append('\\', backslashes);
            sb.Append(c);
            backslashes = 0;
        }

        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void Validate(string exe, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> env)
    {
        ArgumentException.ThrowIfNullOrEmpty(exe);
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        if (exe.Contains('\0') || cwd.Contains('\0') || args.Any(a => a.Contains('\0')))
        {
            throw new ArgumentException("A NUL character cannot be passed to a process.");
        }

        if (env.Any(p => p.Key.Length == 0 || p.Key.Contains('=') || p.Key.Contains('\0') || p.Value.Contains('\0')))
        {
            throw new ArgumentException("An environment entry is not valid.", nameof(env));
        }
    }
}

/// <summary>
/// What both platforms share: the wait thread, the exit task and a bounded, non-blocking disposal. <c>log</c> takes one line
/// for the agent's log (error type names only, never content); null where nobody listens.
/// </summary>
internal abstract class RunProcessBase(Stream stdout, Stream stderr, Action<string>? log = null) : IRunProcess
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource<RunExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public Stream Stdout { get; } = stdout;
    public Stream Stderr { get; } = stderr;
    public Task<RunExit> Exited => _exited.Task;

    public abstract void Kill();
    public abstract void KillNow();

    /// <summary>Blocks until the lead process is gone, on the wait thread.</summary>
    protected abstract RunExit WaitForExit();

    /// <summary>Frees what the OS handed out. Called once, from <see cref="DisposeAsync"/>.</summary>
    protected virtual void ReleaseNative()
    {
    }

    protected void BeginWait()
    {
        var thread = new Thread(() =>
        {
            try
            {
                _exited.SetResult(WaitForExit());
            }
            catch (Exception e)
            {
                _exited.SetException(e); // reported to whoever awaits Exited
            }
        })
        { IsBackground = true, Name = "cm-run-wait" };
        thread.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            try
            {
                KillNow();
            }
            catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
            {
                log?.Invoke($"kill on dispose failed ({e.GetType().Name})"); // the message may hold a path: not logged
            }

            await Task.WhenAny(_exited.Task, Task.Delay(CloseWait));
        }
        finally
        {
            // Whatever happened above, the native handles, the tracker thread and the pipes are released.
            try
            {
                ReleaseNative();
            }
            finally
            {
                // A read blocked on a pipe that an escaped process still holds does not end by itself, and closing the stream waits
                // for it: close in the background and give up after a while. The thread is freed when that process dies.
                var close = Task.Run(() =>
                {
                    Stdout.Dispose();
                    Stderr.Dispose();
                });
                if (await Task.WhenAny(close, Task.Delay(CloseWait)) == close) await close;
            }
        }
    }
}
