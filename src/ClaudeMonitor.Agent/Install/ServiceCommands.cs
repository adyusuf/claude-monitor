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
            stderr.WriteLine(Strings.T(Msg.ServiceServerOption, ServerOption, server));
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
                stderr.WriteLine(Strings.T(Msg.ServiceRunAccountWindows, RunAccountOption));
                return 2;
            }

            if (!RunAccount.IsValidName(runAccount))
            {
                stderr.WriteLine(Strings.T(Msg.ServiceRunAccountPlain, RunAccountOption, runAccount));
                return 2;
            }
        }

        var runner = run ?? ServiceInstaller.RunTool;
        var legacy = ServiceLayout.For(AgentConfig.Os);
        var aipod = ServiceLayout.ForAipod(AgentConfig.Os);
        // A service that exists keeps the names it has (an upgrade in place); a fresh install gets the aipod names (ADR-0013, tier 2).
        var onLegacy = !ServiceInstaller.IsInstalled(AgentConfig.Os, aipod, runner) && ServiceInstaller.IsInstalled(AgentConfig.Os, legacy, runner);
        var layout = (onLegacy ? legacy : aipod) with { Server = server };
        // Without --exec a reinstall keeps the level an admin chose before; a first install starts off.
        if (Array.IndexOf(args, ExecOption) < 0) level = ExecPolicyLoader.ReadExisting(layout.ExecConfigPath)?.Level ?? ExecLevels.Off;
        if (level == ExecLevels.Shell) stdout.WriteLine(Strings.T(Msg.ServiceShellWarning));
        if (onLegacy) stdout.WriteLine(Strings.T(Msg.ServiceOldNames));
        return new ServiceInstaller(stdout, runner, AgentConfig.Os, isAdmin ?? ExecPolicyLoader.RunningAsRoot(), layout)
            .Install(Environment.ProcessPath!, level, Array.IndexOf(args, AllowRootFlag) >= 0, runAccount);
    }

    /// <summary>Removes the service under whichever names it has; one that is half way through a migration (both exist) is removed under both.</summary>
    public static int Uninstall(TextWriter stdout, Func<string, IReadOnlyList<string>, int>? run = null, bool? isAdmin = null)
    {
        var runner = run ?? ServiceInstaller.RunTool;
        var admin = isAdmin ?? ExecPolicyLoader.RunningAsRoot();
        var layouts = new[] { ServiceLayout.ForAipod(AgentConfig.Os), ServiceLayout.For(AgentConfig.Os) }
            .Where(l => ServiceInstaller.IsInstalled(AgentConfig.Os, l, runner)).ToList();
        if (layouts.Count == 0) layouts.Add(ServiceLayout.For(AgentConfig.Os)); // none: the old one says "not installed" the way it always did
        var worst = 0;
        foreach (var l in layouts) worst = Math.Max(worst, new ServiceInstaller(stdout, runner, AgentConfig.Os, admin, l).Uninstall());
        return worst;
    }

    /// <summary>
    /// "service migrate [--dry-run | --undo | --finish [--now]]" (ADR-0013, tier 2): moves a boot service to the aipod names, goes back, or removes the old service for good.
    /// Like install --service it runs before the user's home is touched.
    /// </summary>
    public static int Service(string[] args, TextWriter stdout, TextWriter stderr, Func<string, IReadOnlyList<string>, int>? run = null, bool? isAdmin = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var modes = new[] { MigrateDryRun, MigrateUndo, MigrateFinish }.Count(m => Array.IndexOf(args, m) >= 0);
        if (args.Length < 2 || args[1] != MigrateVerb || modes > 1 || args.Skip(2).Any(a => a is not (MigrateDryRun or MigrateUndo or MigrateFinish or MigrateNow)) || (Array.IndexOf(args, MigrateNow) >= 0 && Array.IndexOf(args, MigrateFinish) < 0))
        {
            stderr.WriteLine(Strings.T(Msg.ServiceMigrateUsage, MigrateVerb, MigrateDryRun, MigrateUndo, MigrateFinish, MigrateNow));
            return 2;
        }

        var migrator = new ServiceMigrator(stdout, run ?? ServiceInstaller.RunTool, AgentConfig.Os, isAdmin ?? ExecPolicyLoader.RunningAsRoot(), ServiceLayout.For(AgentConfig.Os), ServiceLayout.ForAipod(AgentConfig.Os));
        if (Array.IndexOf(args, MigrateUndo) >= 0) return migrator.GoBack();
        if (Array.IndexOf(args, MigrateFinish) >= 0) return migrator.Finish(Array.IndexOf(args, MigrateNow) >= 0);
        return migrator.Migrate(Array.IndexOf(args, MigrateDryRun) >= 0);
    }

    public const string MigrateVerb = "migrate";
    public const string MigrateDryRun = "--dry-run";
    public const string MigrateUndo = "--undo";
    public const string MigrateFinish = "--finish";
    public const string MigrateNow = "--now";

    /// <summary>"install --exec off|argv|shell" for an interactive agent: saved in agent.json, read by the daemon at its next profile.</summary>
    public static int SaveExecLevel(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var level = Level(args, stderr);
        if (level is null) return 2;
        var identity = Identity.Load(config);
        (identity with { ExecLevel = level }).Save(config);
        stdout.WriteLine(Strings.T(Msg.ServiceExecIs, level));
        return 0;
    }

    /// <summary>The --exec value, off when absent; null (after an error message) when it is not one of the levels.</summary>
    private static string? Level(string[] args, TextWriter stderr)
    {
        if (Array.IndexOf(args, ExecOption) < 0) return ExecLevels.Off;
        var value = Cli.Option(args, ExecOption);
        if (value is not null && ExecLevels.All.Contains(value)) return value;
        stderr.WriteLine(Strings.T(Msg.ServiceExecOption, ExecOption, string.Join(", ", ExecLevels.All), value));
        return null;
    }
}
