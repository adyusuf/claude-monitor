using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>Why the target refused a run; sent back as the run's error.</summary>
public static class ExecErrors
{
    public const string ExecLevel = "exec_level";
    public const string Malformed = "run_malformed";
    public const string Timeout = "timeout_invalid";
    public const string ShellCwd = "shell_cwd";
    public const string GrantMismatch = "grant_mismatch";
    public const string ExeNotAbsolute = "exe_not_absolute";
    public const string ExeUnresolved = "exe_unresolved";
    public const string ExeNeverGrantable = "exe_never_grantable";
    public const string ExeUntrusted = "exe_untrusted";
    public const string ExeNotAllowed = "exe_not_allowed";
    public const string PathUnresolved = "path_unresolved";
    public const string PathEscape = "path_escape";
    public const string PathInHome = "path_in_home";
    public const string PathHardLink = "path_hard_link";
    public const string RootForbidden = "root_forbidden";
    public const string RootNotAllowed = "root_not_allowed";
}

/// <summary>
/// The target's full check just before exec (ADR-0004, "Grants: full templates, checked twice"): the exec level, the
/// grant template again, then the real paths of the program, the working directory and every path value.
/// </summary>
public static class ExecGuard
{
    private const string UnixShell = "/bin/sh";
    private const string WindowsShell = "cmd.exe";
    private const string WindowsSystemFallback = @"C:\Windows\System32";

    public static ExecDecision Check(RunMessage run, ExecPolicy policy, string os, string agentHome)
    {
        if (run is null || policy is null || !policy.Allows(run.Mode))
        {
            return ExecDecision.Refuse(ExecErrors.ExecLevel);
        }

        if (!OsKinds.All.Contains(os))
        {
            return ExecDecision.Refuse(GrantErrors.UnknownOs);
        }

        if (run.TimeoutSeconds is < GrantMatcher.MinTimeoutSeconds or > GrantMatcher.MaxTimeoutSecondsLimit)
        {
            return ExecDecision.Refuse(ExecErrors.Timeout);
        }

        return run.Mode switch
        {
            RunModes.Shell => CheckShell(run, os),
            RunModes.Argv => CheckArgv(run, policy, os, agentHome),
            _ => ExecDecision.Refuse(ExecErrors.ExecLevel),
        };
    }

    // The shell runs in the service home, chosen by the executor; a caller never picks its directory or a grant for it.
    private static ExecDecision CheckShell(RunMessage run, string os)
    {
        if (run.Grant is not null || run.GrantId is not null)
        {
            return ExecDecision.Refuse(ExecErrors.GrantMismatch);
        }

        if (string.IsNullOrEmpty(run.ShellCommand))
        {
            return ExecDecision.Refuse(ExecErrors.Malformed);
        }

        return run.Cwd is not null ? ExecDecision.Refuse(ExecErrors.ShellCwd) : new ExecDecision(true, null, ShellPath(os));
    }

    private static string ShellPath(string os)
    {
        if (os != OsKinds.Windows)
        {
            return UnixShell;
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return (string.IsNullOrEmpty(system) ? WindowsSystemFallback : system) + @"\" + WindowsShell;
    }

    private static ExecDecision CheckArgv(RunMessage run, ExecPolicy policy, string os, string agentHome)
    {
        var argv = run.Argv;
        if (argv is null || argv.Count == 0 || string.IsNullOrEmpty(argv[0]))
        {
            return ExecDecision.Refuse(ExecErrors.Malformed);
        }

        IReadOnlyList<GrantPathUse> uses = [];
        if (run.Grant is not null)
        {
            if (GrantMatcher.Validate(run.Grant, os, false) is not null || !GrantMatcher.Matches(run.Grant, argv, run.Cwd, run.TimeoutSeconds, os))
            {
                return ExecDecision.Refuse(ExecErrors.GrantMismatch);
            }

            uses = GrantMatcher.PathUses(run.Grant, argv, os);
        }
        else if (run.GrantId is not null)
        {
            return ExecDecision.Refuse(ExecErrors.GrantMismatch); // a grant id with no template to check it against
        }
        else if (!GrantMatcher.IsAbsoluteExecutable(argv[0], os))
        {
            return ExecDecision.Refuse(ExecErrors.ExeNotAbsolute);
        }

        var exe = CheckExecutable(argv[0], run.Grant is not null, policy, os);
        if (exe.Error is not null)
        {
            return ExecDecision.Refuse(exe.Error);
        }

        var error = CheckPaths(run.Cwd ?? run.Grant?.Cwd, uses, policy, os, agentHome);
        return error is null ? new ExecDecision(true, null, exe.Path) : ExecDecision.Refuse(error);
    }

    private static (string? Path, string? Error) CheckExecutable(string argv0, bool fromGrant, ExecPolicy policy, string os)
    {
        var exe = ExecPaths.RealPath(argv0, os, allowMissing: false);
        if (exe is null || !File.Exists(exe) || (os == OsKinds.Windows && !GrantMatcher.IsAbsoluteExecutable(exe, os)))
        {
            return (null, ExecErrors.ExeUnresolved);
        }

        // A per-call run of an interpreter is the owner's decision (the web warns); a standing grant never covers one.
        if (fromGrant && GrantMatcher.IsInterpreter(exe, os))
        {
            return (null, ExecErrors.ExeNeverGrantable);
        }

        if (!ExecPaths.IsTrustedExecutable(exe, os))
        {
            return (null, ExecErrors.ExeUntrusted);
        }

        if (policy.AllowedExecutables.Count > 0 && !ResolveAll(policy.AllowedExecutables, os).Any(allowed => SamePath(allowed, exe, os)))
        {
            return (null, ExecErrors.ExeNotAllowed);
        }

        return (exe, null);
    }

    private static string? CheckPaths(string? cwd, IReadOnlyList<GrantPathUse> uses, ExecPolicy policy, string os, string agentHome)
    {
        var home = ExecPaths.RealPath(agentHome, os, allowMissing: true) ?? agentHome;
        var roots = ResolveAll(policy.AllowedRoots, os);
        var limited = policy.AllowedRoots.Count > 0;

        if (cwd is not null)
        {
            var realCwd = ExecPaths.RealPath(cwd, os, allowMissing: false);
            if (realCwd is null)
            {
                return ExecErrors.PathUnresolved;
            }

            var cwdError = CheckAgainstHomeAndPolicy(realCwd, home, roots, limited, os);
            if (cwdError is not null)
            {
                return cwdError;
            }
        }

        foreach (var use in uses)
        {
            var realRoot = ExecPaths.RealPath(use.Root, os, allowMissing: false);
            var realValue = ExecPaths.RealPath(use.Value, os, allowMissing: true);
            if (realRoot is null || realValue is null)
            {
                return ExecErrors.PathUnresolved;
            }

            // The root is judged again after its links are followed: a link must not lead it to /etc or the home.
            if (GrantMatcher.IsForbiddenRoot(realRoot, os))
            {
                return ExecErrors.RootForbidden;
            }

            if (!GrantMatcher.IsUnderRoot(realValue, realRoot, os))
            {
                return ExecErrors.PathEscape;
            }

            var valueError = CheckAgainstHomeAndPolicy(realValue, home, roots, limited, os);
            if (valueError is not null)
            {
                return valueError;
            }

            if (ExecPaths.HasExtraLinks(realValue, os))
            {
                return ExecErrors.PathHardLink;
            }
        }

        return null;
    }

    private static string? CheckAgainstHomeAndPolicy(string real, string home, List<string> roots, bool limited, string os)
    {
        if (GrantMatcher.IsUnderRoot(real, home, os))
        {
            return ExecErrors.PathInHome;
        }

        return limited && !roots.Any(root => GrantMatcher.IsUnderRoot(real, root, os)) ? ExecErrors.RootNotAllowed : null;
    }

    private static List<string> ResolveAll(IReadOnlyList<string> paths, string os) =>
        paths.Select(path => ExecPaths.RealPath(path, os, allowMissing: false)).OfType<string>().ToList();

    private static bool SamePath(string a, string b, string os) => string.Equals(a, b, os == OsKinds.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
