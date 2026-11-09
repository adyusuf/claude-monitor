using Podium.Agent.Access;
using Podium.Contracts;

namespace Podium.Agent.Exec;

// The argv part of ExecGuard: the grant template again, the program's real path and trust, the working directory and every path value.
public static partial class ExecGuard
{
    private static ExecDecision CheckArgv(RunMessage run, ExecPolicy policy, string os, string agentHome)
    {
        var argv = run.Argv;
        if (argv is null || argv.Count == 0 || string.IsNullOrEmpty(argv[0]))
        {
            return ExecDecision.Refuse(ExecErrors.Malformed);
        }

        if (NeedsCodeNow(run, policy, os)) return ExecDecision.Refuse(RemoteErrors.TotpRequired);
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

        var (error, cwd, values) = CheckPaths(run.Cwd ?? run.Grant?.Cwd, uses, policy, os, agentHome);
        return error is null ? new ExecDecision(true, null, exe.Path, cwd, values) : ExecDecision.Refuse(error);
    }

    private static (string? Path, string? Error) CheckExecutable(string argv0, bool fromGrant, ExecPolicy policy, string os)
    {
        var exe = PathIdentity.Resolve(argv0, os, allowMissing: false);
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

    /// <summary>Null and the real working directory and path values when they pass, else the refusal.</summary>
    private static (string? Error, string? Cwd, List<string> Values) CheckPaths(string? cwd, IReadOnlyList<GrantPathUse> uses, ExecPolicy policy, string os,
        string agentHome)
    {
        var home = PathIdentity.Resolve(agentHome, os, allowMissing: true) ?? agentHome;
        var roots = ResolveAll(policy.AllowedRoots, os);
        var limited = policy.AllowedRoots.Count > 0;
        string? realCwd = null;
        var values = new List<string>();

        if (cwd is not null)
        {
            realCwd = PathIdentity.Resolve(cwd, os, allowMissing: false);
            if (realCwd is null)
            {
                return (ExecErrors.PathUnresolved, null, values);
            }

            var cwdError = CheckAgainstHomeAndPolicy(realCwd, home, roots, limited, os);
            if (cwdError is not null)
            {
                return (cwdError, null, values);
            }
        }

        foreach (var use in uses)
        {
            var realRoot = PathIdentity.Resolve(use.Root, os, allowMissing: false);
            var realValue = PathIdentity.Resolve(use.Value, os, allowMissing: true);
            if (realRoot is null || realValue is null)
            {
                return (ExecErrors.PathUnresolved, null, values);
            }

            // The root is judged again after its links are followed: a link must not lead it to /etc or the home.
            if (GrantMatcher.IsForbiddenRoot(realRoot, os))
            {
                return (ExecErrors.RootForbidden, null, values);
            }

            if (!GrantMatcher.IsUnderRoot(realValue, realRoot, os))
            {
                return (ExecErrors.PathEscape, null, values);
            }

            var valueError = CheckAgainstHomeAndPolicy(realValue, home, roots, limited, os);
            if (valueError is not null)
            {
                return (valueError, null, values);
            }

            if (ExecPaths.HasExtraLinks(realValue, os))
            {
                return (ExecErrors.PathHardLink, null, values);
            }

            values.Add(realValue);
        }

        return (null, realCwd, values);
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
        paths.Select(path => PathIdentity.Resolve(path, os, allowMissing: false)).OfType<string>().ToList();

    private static bool SamePath(string a, string b, string os) => string.Equals(a, b, os == OsKinds.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
