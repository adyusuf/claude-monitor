using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>The command line of the standalone agent (ADR-0008 §3g): "install --standalone [--server url] [--exec ...]" and "uninstall --standalone".</summary>
public static class StandaloneCommands
{
    public const string StandaloneFlag = "--standalone";

    public static bool IsStandalone(string[] args) => Array.IndexOf(args ?? [], StandaloneFlag) >= 0;

    public static async Task<int> InstallAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock,
        Func<string, IReadOnlyList<string>, int>? run = null, bool? isRoot = null, Func<string?, Task<int>>? login = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var server = Cli.Option(args, ServiceCommands.ServerOption)?.TrimEnd('/');
        if (server is not null && (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                                   || server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '%' or '|')))
        {
            stderr.WriteLine($"{ServiceCommands.ServerOption} takes the https address of the aipod server, not \"{server}\".");
            return 2;
        }

        if (Array.IndexOf(args, ServiceCommands.ExecOption) >= 0 && ServiceCommands.SaveExecLevel(args, config, stdout, stderr) != 0) return 2;
        var installer = Installer(config, stdout, run, isRoot, login ?? (s => new Login(config, stdout, clock).RunAsync(s, null, CancellationToken.None)));
        var code = await installer.InstallAsync(Environment.ProcessPath!, server ?? config.ServerOverride ?? Identity.Peek(config)?.Server);
        return code;
    }

    public static int Uninstall(AgentConfig config, TextWriter stdout, Func<string, IReadOnlyList<string>, int>? run = null, bool? isRoot = null) =>
        Installer(config, stdout, run, isRoot, _ => Task.FromResult(1)).Uninstall();

    private static StandaloneInstaller Installer(AgentConfig config, TextWriter stdout, Func<string, IReadOnlyList<string>, int>? run, bool? isRoot,
        Func<string?, Task<int>> login)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // New installs use the aipod names; the old layout is passed so that an install replaces a registration under the old names and an uninstall removes either (ADR-0013, tier 2).
        var layout = StandaloneLayout.ForAipod(AgentConfig.Os, profile, config.Home, Environment.UserName);
        return new StandaloneInstaller(stdout, run ?? ServiceInstaller.RunTool, ServiceInstaller.CaptureTool, layout, isRoot ?? ExecPolicyLoader.RunningAsRoot(),
            () => Identity.Peek(config)?.Connected == true, login, () => OperatingSystem.IsWindows() ? null : UnixFiles.Euid(), AgentConfig.Version,
            StandaloneLayout.For(AgentConfig.Os, profile, config.Home, Environment.UserName));
    }
}
