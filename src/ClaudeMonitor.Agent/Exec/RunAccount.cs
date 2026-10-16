using System.Text.RegularExpressions;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// The separate run account of a service agent (ADR-0010 §5), Linux and macOS: a second, unprivileged, non-login account that runs the remote work, so a run cannot
/// read the service's home, its credential file or its policy. The service starts a run through <c>sudo -n -u &lt;account&gt;</c> under a drop-in that allows exactly that
/// and nothing else; the run's folder and working directory are a shared folder only the service and the run account can use. It is a smaller blast radius, not a sandbox.
/// </summary>
public static partial class RunAccount
{
    public const string WorkFolderName = "run-work";
    public const string Sudo = "/usr/bin/sudo";
    public const string Env = "/usr/bin/env";
    public const string Sh = "/bin/sh";
    public const string SudoersFileName = "cm-agent-run";

    /// <summary>The group-writable mask the run works under, so the service (a member of the run account's group) can always clean up after it.</summary>
    public const string Umask = "007";

    /// <summary>The default account name for an OS: <c>cm-run</c> on Linux, <c>_cmrun</c> on macOS; null where a run account is not supported.</summary>
    public static string? DefaultName(string os) => os switch
    {
        OsKinds.Linux => "cm-run",
        OsKinds.MacOs => "_cmrun",
        _ => null,
    };

    /// <summary>A plain account name: letters, digits, underscore and dash, starting with a letter or underscore, at most 32 characters. Never root or an admin name.</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 32 } && NamePattern().IsMatch(name) && !Reserved.Contains(name.ToLowerInvariant());

    private static readonly string[] Reserved = ["root", "wheel", "admin", "sudo", "daemon", "nobody", "system"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex NamePattern();

    /// <summary>The folder the run account shares with the service: its scratch folders and the working directory of a run that names none.</summary>
    public static string WorkFolder(string home) => Path.Combine(home, WorkFolderName);

    /// <summary>
    /// The command that starts <paramref name="exe"/> as <paramref name="account"/>: <c>sudo -n -u acct -- env -i VAR=value... sh -c 'umask 007; exec "$@"' sh exe args</c>.
    /// <c>env -i</c> gives the run exactly <paramref name="env"/> whatever sudo's own handling is; the shell only sets the mask and execs, so no text of the run goes through it.
    /// </summary>
    public static (string Exe, IReadOnlyList<string> Argv) Wrap(string account, string exe, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env)
    {
        if (!IsValidName(account)) throw new ArgumentException("not a valid run account name", nameof(account));
        ArgumentNullException.ThrowIfNull(env);
        List<string> wrapped = [Sudo, "-n", "-u", account, "--", Env, "-i"];
        foreach (var (name, value) in env) wrapped.Add($"{name}={value}");
        wrapped.AddRange([Sh, "-c", $"umask {Umask}; exec \"$@\"", "sh", exe]);
        wrapped.AddRange(argv.Skip(1)); // argv[0] is the program's own name; the new process is started from exe
        return (Sudo, wrapped);
    }

    /// <summary>What sudo itself runs with: nothing but a fixed PATH.</summary>
    public static IReadOnlyDictionary<string, string> SudoEnvironment { get; } = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" };

    /// <summary>
    /// The sudoers drop-in: only the service account may run commands, only as only the run account, with no password and no terminal. One line each, no wildcard
    /// for either name (both are validated names), and nothing that lets the run account become anyone else.
    /// </summary>
    public static string SudoersText(string serviceAccount, string runAccount)
    {
        if (!IsValidName(runAccount) || !NamePattern().IsMatch(serviceAccount)) throw new ArgumentException("not a valid account name");
        return $"# Claude Monitor: the agent's service account starts remote runs as the run account, and nothing else.\n{serviceAccount} ALL=({runAccount}) NOPASSWD: ALL\nDefaults:{serviceAccount} !requiretty\nDefaults:{serviceAccount} !use_pty\n";
    }
}
