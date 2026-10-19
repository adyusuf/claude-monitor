using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// The command-line side of remote work (ADR-0005): "install --service" / "uninstall --service" for a boot service, and
/// "install --exec" for an interactive agent. They run before the user's home is touched, so an admin's run never
/// leaves root-owned files in its own home.
/// </summary>
public static class ServiceCommands
{
    public const string ServiceFlag = "--service";
    public const string ExecOption = "--exec";
    public const string AllowRootFlag = "--allow-root";
    public const string RunAccountOption = "--run-account";
    public const string ServerOption = "--server";

    public static bool IsService(string[] args) => Array.IndexOf(args ?? [], ServiceFlag) >= 0;

    public static int Install(string[] args, TextWriter stdout, TextWriter stderr, Func<string, IReadOnlyList<string>, int>? run = null,
        bool? isAdmin = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var level = Level(args, stderr);
        if (level is null) return 2;
        var server = Cli.Option(args, ServerOption)?.TrimEnd('/');
        if (server is not null && (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                                   || server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '%' or '|')))
        {
            stderr.WriteLine($"{ServerOption} takes the https address of the aipod server, not \"{server}\".");
            return 2;
        }

        string? runAccount = null;
        if (Array.IndexOf(args, RunAccountOption) >= 0)
        {
            // The name is optional: the next argument when it is not another option, else the default of this OS (cm-run, _cmrun).
            var at = Array.IndexOf(args, RunAccountOption);
            runAccount = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : RunAccount.DefaultName(AgentConfig.Os);
            if (runAccount is null)
            {
                stderr.WriteLine($"{RunAccountOption} is supported on Linux and macOS only (ADR-0010 §5).");
                return 2;
            }

            if (!RunAccount.IsValidName(runAccount))
            {
                stderr.WriteLine($"{RunAccountOption} takes a plain account name (letters, digits, underscore, dash), not \"{runAccount}\".");
                return 2;
            }
        }

        var layout = ServiceLayout.For(AgentConfig.Os) with { Server = server };
        // Without --exec a reinstall keeps the level an admin chose before; a first install starts off.
        if (Array.IndexOf(args, ExecOption) < 0) level = ExecPolicyLoader.ReadExisting(layout.ExecConfigPath)?.Level ?? ExecLevels.Off;
        if (level == ExecLevels.Shell) stdout.WriteLine("Warning: exec level shell lets approved runs execute any shell text on this machine.");
        return new ServiceInstaller(stdout, run ?? ServiceInstaller.RunTool, AgentConfig.Os, isAdmin ?? ExecPolicyLoader.RunningAsRoot(), layout)
            .Install(Environment.ProcessPath!, level, Array.IndexOf(args, AllowRootFlag) >= 0, runAccount);
    }

    public static int Uninstall(TextWriter stdout, Func<string, IReadOnlyList<string>, int>? run = null, bool? isAdmin = null) =>
        new ServiceInstaller(stdout, run ?? ServiceInstaller.RunTool, AgentConfig.Os, isAdmin ?? ExecPolicyLoader.RunningAsRoot()).Uninstall();

    /// <summary>"install --exec off|argv|shell" for an interactive agent: saved in agent.json, read by the daemon at its next profile.</summary>
    public static int SaveExecLevel(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var level = Level(args, stderr);
        if (level is null) return 2;
        var identity = Identity.Load(config);
        (identity with { ExecLevel = level }).Save(config);
        stdout.WriteLine($"Remote runs on this machine: {level} (the workspace switch and an owner's approval still apply).");
        return 0;
    }

    /// <summary>The --exec value, off when absent; null (after an error message) when it is not one of the levels.</summary>
    private static string? Level(string[] args, TextWriter stderr)
    {
        if (Array.IndexOf(args, ExecOption) < 0) return ExecLevels.Off;
        var value = Cli.Option(args, ExecOption);
        if (value is not null && ExecLevels.All.Contains(value)) return value;
        stderr.WriteLine($"{ExecOption} takes {string.Join(", ", ExecLevels.All)}, not \"{value}\".");
        return null;
    }
}
