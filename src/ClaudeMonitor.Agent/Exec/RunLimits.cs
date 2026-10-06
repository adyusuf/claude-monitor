using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// Resource limits of one run on macOS (ADR-0005, "The executor on the target"): CPU seconds, the largest file it may write
/// and open descriptors; core files are always off. They are hard limits, so the run cannot raise them again. A limit the
/// system refuses fails the run (exit 126); it never runs unlimited.
/// </summary>
public sealed record RunLimits(int CpuSeconds, long FileBytes, int OpenFiles)
{
    // Bounds on the configured values: kern.maxfilesperproc is at least 10240 on every supported macOS.
    public const long MinFileBytes = 4096;
    public const int MinOpenFiles = 64;
    public const int MaxOpenFiles = 10240;
    public const int MaxCpuCores = 256;

    /// <summary>
    /// macOS only. posix_spawn has no rlimit attribute there and setrlimit on the daemon itself is unsafe (the CPU limit is
    /// cumulative and would signal the daemon). On Linux the systemd unit carries the limits (the Limit* settings) and the
    /// cgroup, and /bin/sh there is often dash, which cannot keep argv[0] through exec.
    /// </summary>
    public static bool AppliesHere => OperatingSystem.IsMacOS();

    /// <summary>
    /// The CPU budget is the run's wall time plus the kill grace, times the cores a run may use: a multi-threaded run that
    /// stays inside its timeout never meets it, while a process that escaped the kill cannot burn CPU for ever.
    /// </summary>
    public static RunLimits For(AgentConfig config, TimeSpan timeout)
    {
        var wall = Math.Ceiling((timeout + config.ExecKillGrace).TotalSeconds);
        var cores = Math.Clamp(config.ExecCpuCores, 1, MaxCpuCores);
        return new RunLimits(
            (int)Math.Clamp(wall * cores, 1, int.MaxValue),
            Math.Max(config.ExecFileSizeMax, MinFileBytes),
            Math.Clamp(config.ExecOpenFilesMax, MinOpenFiles, MaxOpenFiles));
    }
}

/// <summary>
/// The trusted launcher: /bin/sh sets the limits and then execs the target in the same process, so the pid, the process
/// group and the kill logic stay those of the run. The target comes in as "$0", its argv[0] as "$1" and its arguments as
/// "$@": they are never re-read by the shell (no splitting, no expansion). The numbers are formatted here and nothing from
/// the requester is part of the script text. The shell adds SHLVL=0 to the environment when it execs.
/// </summary>
public static class RunLauncher
{
    public const string Shell = "/bin/sh";
    private const string Name = "sh";
    private const int ExitLimitRefused = 126;
    // macOS bash counts ulimit -f in 1024-byte blocks (512 only with POSIXLY_CORRECT, which the empty run environment never
    // sets); a test writes past the limit to catch a change of that unit.
    private const int BlockBytes = 1024;

    /// <summary>The program and argv to start instead of exe and argv (argv[0] first) so that the limits apply first.</summary>
    public static (string Exe, IReadOnlyList<string> Argv) Wrap(string exe, IReadOnlyList<string> argv, RunLimits limits)
    {
        ArgumentException.ThrowIfNullOrEmpty(exe);
        if (argv.Count == 0) throw new ArgumentException("argv[0] is missing.", nameof(argv));
        var blocks = (limits.FileBytes + BlockBytes - 1) / BlockBytes;

        // unset PWD: the shell would export its own; exec -a keeps argv[0]; "--" so that nothing after it is an option.
        var script = FormattableString.Invariant(
            $"ulimit -t {limits.CpuSeconds} -f {blocks} -c 0 -n {limits.OpenFiles} || exit {ExitLimitRefused}; unset PWD; a=$1; shift; exec -a \"$a\" -- \"$0\" \"$@\"");
        return (Shell, [Name, "-c", script, exe, .. argv]);
    }
}
