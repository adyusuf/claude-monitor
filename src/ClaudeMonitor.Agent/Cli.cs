using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeMonitor.Agent;

/// <summary>The command line. Each command is a thin call into its own class.</summary>
public static class Cli
{
    public const string Usage = """
        cm-agent login --server <url>   connect this machine (shows a code to approve on the web)
        cm-agent install                register the Claude Code plugin (hooks + MCP) that starts the agent
        cm-agent status                 connection and queue
        cm-agent logout | uninstall | version
        (cm-agent hook <Event> | mcp | daemon are started by Claude Code and the agent itself)
        """;

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextReader stdin, TextWriter stdout, TextWriter stderr,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(args);
        var log = new AgentLog(config, clock);
        switch (args.FirstOrDefault())
        {
            case "hook" when args.Length > 1:
                return await HookAsync(args[1], config, stdin, stdout, stderr, clock, log);
            case "mcp":
                DaemonHost.EnsureRunning(config, log);
                await McpAsync(config, clock);
                return 0;
            case "daemon":
                using (var stop = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        stop.Cancel();
                    };
                    return await new DaemonHost(config, clock, log).RunAsync(stop.Token);
                }

            case "login":
                var server = Option(args, "--server");
                var code = await new Login(config, stdout, clock).RunAsync(server, null, CancellationToken.None);
                if (code == 0) DaemonHost.EnsureRunning(config, log);
                return code;
            case "logout":
                return await new Login(config, stdout, clock).LogoutAsync();
            case "install":
                return new PluginInstaller(config, stdout).Install(Environment.ProcessPath!);
            case "uninstall":
                return new PluginInstaller(config, stdout).Uninstall();
            case "status":
                return await StatusAsync(config, stdout);
            case "version":
                await stdout.WriteLineAsync(AgentConfig.Version);
                return 0;
            default:
                await stdout.WriteLineAsync(Usage);
                return args.Length == 0 ? 0 : 2;
        }
    }

    /// <summary>A hook never fails its session: whatever happens, exit 0 (project rule).</summary>
    public static async Task<int> HookAsync(string hookEvent, AgentConfig config, TextReader stdin, TextWriter stdout,
        TextWriter stderr, TimeProvider clock, AgentLog log)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            config.EnsureHome();
            var input = await stdin.ReadToEndAsync();
            string? output;
            using (var store = new LocalStore(config.DatabasePath))
            {
                output = await new HookRunner(config, store, clock).RunAsync(hookEvent, input, CancellationToken.None);
            }

            if (Identity.Load(config).Connected) DaemonHost.EnsureRunning(config, log);
            if (output is not null) await stdout.WriteAsync(output);
        }
        catch (Exception e)
        {
            await stderr.WriteLineAsync($"cm-agent hook {hookEvent}: {e.GetType().Name}: {e.Message}");
            log.Write($"hook {hookEvent} failed: {e.GetType().Name} {e.Message}");
        }

        return 0;
    }

    public static async Task<int> StatusAsync(AgentConfig config, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var identity = Identity.Load(config);
        using var store = new LocalStore(config.DatabasePath);
        using var probe = DaemonHost.TryLock(config.LockPath);
        await stdout.WriteLineAsync(identity.Connected ? $"connected: {identity.Server}" : "not connected (cm-agent login --server <url>)");
        await stdout.WriteLineAsync($"daemon: {(probe is null ? "running" : "not running")}");
        await stdout.WriteLineAsync($"events waiting: {store.OutboxCount()}");
        await stdout.WriteLineAsync($"version: {AgentConfig.Version}");
        return identity.Connected ? 0 : 1;
    }

    public static string? Option(string[] args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The MCP server on stdio. Its log goes to stderr: stdout belongs to the protocol.</summary>
    private static async Task McpAsync(AgentConfig config, TimeProvider clock)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(clock);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<MonitorTools>();
        await builder.Build().RunAsync();
    }
}
