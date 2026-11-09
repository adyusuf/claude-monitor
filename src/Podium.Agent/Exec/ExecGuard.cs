using Podium.Agent.Access;
using Podium.Contracts;

namespace Podium.Agent.Exec;

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
/// The target's full check just before exec (ADR-0005, "Grants: full templates, checked twice"): the exec level, the
/// grant template again, then the real paths of the program, the working directory and every path value, and last the
/// access layers (ADR-0020 §4) on those real paths. The argv and path part is in ExecGuard.Paths.cs, the access part in
/// ExecGuard.Access.cs.
/// </summary>
public static partial class ExecGuard
{
    private const string UnixShell = "/bin/sh";
    private const string WindowsShell = "cmd.exe";
    private const string WindowsSystemFallback = @"C:\Windows\System32";

    public static ExecDecision Check(RunMessage run, ExecPolicy policy, string os, string agentHome, Func<string, string?>? toolPath = null,
        Func<string, bool>? trusted = null, IReadOnlyList<AccessLayer>? access = null)
    {
        var decision = CheckRun(run, policy, os, agentHome, toolPath, trusted);
        return decision.Allowed && access is not null ? WithAccess(decision, run, access, os) : decision;
    }

    private static ExecDecision CheckRun(RunMessage run, ExecPolicy policy, string os, string agentHome, Func<string, string?>? toolPath,
        Func<string, bool>? trusted)
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
            RunModes.Shell => CheckShell(run, policy, os),
            RunModes.Argv => CheckArgv(run, policy, os, agentHome),
            RunModes.Script => CheckScript(run, policy, os, toolPath ?? (name => ToolInventory.RealPath(name, os, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
                trusted ?? (real => ExecPaths.IsTrustedExecutable(real, os))),
            _ => ExecDecision.Refuse(ExecErrors.ExecLevel),
        };
    }

    /// <summary>A target whose own claim is strict refuses a shell command or an interpreter run not approved with a two-step code (decision §9.13; this catches an API bug, not a compromised API).</summary>
    private static bool NeedsCodeNow(RunMessage run, ExecPolicy policy, string os) =>
        !run.ApprovedWithTotp && RunCodeRule.NeedsCode(run.Mode, run.Argv, os, MachineRoles.IsStrict(policy.ClaimedEnvironment, policy.ClaimedFunction));

    // The shell runs in the service home, chosen by the executor; a caller never picks its directory or a grant for it.
    private static ExecDecision CheckShell(RunMessage run, ExecPolicy policy, string os)
    {
        if (run.Grant is not null || run.GrantId is not null)
        {
            return ExecDecision.Refuse(ExecErrors.GrantMismatch);
        }

        if (string.IsNullOrEmpty(run.ShellCommand))
        {
            return ExecDecision.Refuse(ExecErrors.Malformed);
        }

        if (NeedsCodeNow(run, policy, os)) return ExecDecision.Refuse(RemoteErrors.TotpRequired);
        return run.Cwd is not null ? ExecDecision.Refuse(ExecErrors.ShellCwd) : new ExecDecision(true, null, ShellPath(os));
    }

    /// <summary>
    /// A script run (ADR-0008 §3c): the target checks again what the API checked. No grant, no argv, no shell text, no working directory; a
    /// known interpreter and a body within the limits; a target whose own claim is strict refuses a script not approved with a two-step code
    /// (this catches an API bug, not a compromised API); and the interpreter is the trusted tool the inventory reports, resolved to its real
    /// path. The returned executable is that interpreter; the executor writes the script and starts it as an argv run.
    /// </summary>
    private static ExecDecision CheckScript(RunMessage run, ExecPolicy policy, string os, Func<string, string?> toolPath, Func<string, bool> trusted)
    {
        if (run.Grant is not null || run.GrantId is not null) return ExecDecision.Refuse(ExecErrors.GrantMismatch);
        if (run.Argv is not null || run.ShellCommand is not null) return ExecDecision.Refuse(ExecErrors.Malformed);
        if (run.Cwd is not null) return ExecDecision.Refuse(ExecErrors.ShellCwd);
        if (!ScriptInterpreters.All.Contains(run.ScriptInterpreter ?? "")) return ExecDecision.Refuse(ExecErrors.Malformed);
        if (run.ScriptBody is not { Length: > 0 } body || body.Length > ScriptInterpreters.BodyCeiling || body.Contains('\0')) return ExecDecision.Refuse(ExecErrors.Malformed);
        if (run.ScriptArgs is { } args && (args.Count > ScriptInterpreters.ArgsMax || args.Any(a => a is null || a.Length > ScriptInterpreters.ArgChars || a.Contains('\0')))) return ExecDecision.Refuse(ExecErrors.Malformed);
        if (ScriptFiles.Validate(run.ScriptFiles, run.ScriptStdin) is not null) return ExecDecision.Refuse(ExecErrors.Malformed);
        if (MachineRoles.IsStrict(policy.ClaimedEnvironment, policy.ClaimedFunction) && !run.ApprovedWithTotp) return ExecDecision.Refuse(RemoteErrors.TotpRequired);
        foreach (var name in ScriptInterpreters.Tools(run.ScriptInterpreter))
        {
            if (toolPath(name) is { Length: > 0 } real && trusted(real)) return new ExecDecision(true, null, real);
        }

        return ExecDecision.Refuse(RemoteErrors.InterpreterMissing);
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
}
