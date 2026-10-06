using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The shape checks of a run request (ADR-0004), before anything is looked up. They return an error code (an i18n key
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
    public const int ClientKeyMax = 100;
    public const int CwdMax = 1024;

    public static string? Check(RunCreate req, ApiConfig config, string targetOs)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(config);
        if (req.ClientKey is not { Length: >= 1 and <= ClientKeyMax } || HasControl(req.ClientKey)) return BadClientKey;
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
        return null;
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
               && (req.Argv ?? []).SequenceEqual(run.Argv ?? []);
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
}
