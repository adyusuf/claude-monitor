using Podium.Api.Config;
using Podium.Contracts;

namespace Podium.Api.Remote;

/// <summary>
/// The shape checks of a run request (ADR-0005), before anything is looked up. They return an error code (an i18n key
/// the web and the MCP tools show) or null. The target's own checks before exec are stricter; these keep junk out of
/// the database and out of the owner's approval card.
/// </summary>
public static class RunRules
{
    public const string BadClientKey = "invalid_client_key";
    public const string BadMode = "invalid_mode";
    public const string BadCommand = "invalid_command";
    public const string BadCwd = "invalid_cwd";
    public const string BadTimeout = "invalid_timeout";
    public const string BadReason = "invalid_reason";
    public const string BadProject = "invalid_project";
    public const string BadKind = "invalid_kind";
    public const string InvalidScript = RemoteErrors.InvalidScript;
    public const string InvalidInterpreter = RemoteErrors.InvalidInterpreter;
    public const int ClientKeyMax = 100;
    public const int CwdMax = 1024;

    public static string? Check(RunCreate req, ApiConfig config, string targetOs)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(config);
        if (req.ClientKey is not { Length: >= 1 and <= ClientKeyMax } || HasControl(req.ClientKey)) return BadClientKey;
        if (req.Mode == RunModes.Script) return CheckScript(req, config);
        if (req.ScriptBody is not null || req.ScriptInterpreter is not null || req.ScriptArgs is not null || req.ScriptFiles is not null || req.ScriptStdin is not null) return BadCommand;
        var command = req.Mode switch
        {
            RunModes.Argv => req.ShellCommand is null && req.Argv is { Count: > 0 } argv && argv.Count <= config.RunArgvMax
                             && argv.All(a => a is not null && a.Length <= config.RunTextMax && !a.Contains('\0'))
                             && Absolute(argv[0], targetOs),
            RunModes.Shell => req.Argv is null && req.ShellCommand is { Length: > 0 } text && text.Length <= config.RunTextMax
                              && !text.Contains('\0'),
            _ => (bool?)null,
        };
        if (command is null) return BadMode;
        if (command == false) return BadCommand;
        if (req.Cwd is not null && (req.Cwd.Length > CwdMax || HasControl(req.Cwd) || !Absolute(req.Cwd, targetOs))) return BadCwd;
        if (req.Mode == RunModes.Shell && req.Cwd is not null) return BadCwd;
        if (req.TimeoutSeconds is < 1 or > 3600) return BadTimeout;
        if (req.Reason is { } reason && (reason.Length > config.RunReasonMax || reason.Contains('\0'))) return BadReason;
        if (req.ProjectKey is { } project && (project.Length is < 1 or > RunKinds.ProjectKeyMax || HasControl(project))) return BadProject;
        if (req.Kind is { } kind && !RunKinds.All.Contains(kind)) return BadKind;
        return null;
    }

    /// <summary>
    /// A script run (ADR-0008 §3c): a known interpreter, a body of 1 to <see cref="ApiConfig.RunScriptMax"/> characters without NUL, at most
    /// <see cref="ScriptInterpreters.ArgsMax"/> arguments, no argv, no shell text and no working directory (the agent starts it in its scratch
    /// folder). The usual timeout, reason, project and kind rules apply.
    /// </summary>
    private static string? CheckScript(RunCreate req, ApiConfig config)
    {
        if (!ScriptInterpreters.All.Contains(req.ScriptInterpreter ?? "")) return InvalidInterpreter;
        var max = Math.Min(config.RunScriptMax, ScriptInterpreters.BodyCeiling);
        if (req.ScriptBody is not { Length: >= 1 } body || body.Length > max || body.Contains('\0') || string.IsNullOrWhiteSpace(body)) return InvalidScript;
        if (req.ScriptArgs is { } args && (args.Count > ScriptInterpreters.ArgsMax || args.Any(a => a is null || a.Length > ScriptInterpreters.ArgChars || a.Contains('\0')))) return InvalidScript;
        if (ScriptFiles.Validate(req.ScriptFiles, req.ScriptStdin) is { } invalidFiles) return invalidFiles;
        if (req.Argv is not null || req.ShellCommand is not null) return BadCommand;
        if (req.Cwd is not null) return BadCwd;
        if (req.TimeoutSeconds is < 1 or > 3600) return BadTimeout;
        if (req.Reason is { } reason && (reason.Length > config.RunReasonMax || reason.Contains('\0'))) return BadReason;
        if (req.ProjectKey is { } project && (project.Length is < 1 or > RunKinds.ProjectKeyMax || HasControl(project))) return BadProject;
        return req.Kind is { } kind && !RunKinds.All.Contains(kind) ? BadKind : null;
    }

    /// <summary>An absolute path for the target's OS: "/..." on Unix, "C:\..." on Windows (no UNC, no device path).</summary>
    public static bool Absolute(string path, string os) => os switch
    {
        OsKinds.Windows => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\',
        OsKinds.MacOs or OsKinds.Linux => path.StartsWith('/'),
        _ => false,
    };

    /// <summary>The same command as an existing run: a retried request returns it, a different one is a conflict.</summary>
    public static bool SameCommand(RunCreate req, Data.RemoteRun run)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(run);
        return req.TargetAgentId == run.TargetAgentId && req.Mode == run.Mode && req.ShellCommand == run.ShellCommand
               && req.Cwd == run.Cwd && req.TimeoutSeconds == run.TimeoutSeconds
               && (req.Argv ?? []).SequenceEqual(run.Argv ?? [])
               && req.ScriptInterpreter == run.ScriptInterpreter && req.ScriptBody == run.ScriptBody
               && (req.ScriptArgs ?? []).SequenceEqual(run.ScriptArgs ?? [])
               && req.ScriptStdin == run.ScriptStdin
               && (req.ScriptFiles ?? []).OrderBy(f => f.Path, StringComparer.Ordinal).SequenceEqual((run.ScriptFiles ?? []).OrderBy(f => f.Path, StringComparer.Ordinal));
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
}
