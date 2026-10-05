using System.Globalization;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Push;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;
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
        cm-agent install [--stop-wait <seconds>] [--push on|off]
                                        register the Claude Code plugin (hooks + MCP) that starts the agent;
                                        --stop-wait: how long a finished turn waits for a prompt from the web (0-590, default 0)
                                        --push: deliver web messages into an idle session as a Claude Code channel (default off)
        cm-agent status                 connection and queue
        cm-agent logout | uninstall | version
        (cm-agent hook <Event> | mcp | daemon are started by Claude Code and the agent itself)
        """;

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextReader stdin, TextWriter stdout, TextWriter stderr,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(args);
        var log = new AgentLog(config, clock);
        HomeMigration.Run(config, log);
        config = SavedSettings.Apply(config);
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
                return Install(args, config, stdout, stderr, Environment.ProcessPath!);
            case "uninstall":
                return new PluginInstaller(config, stdout).Uninstall();
            case "status":
                return await StatusAsync(config, stdout, clock);
            case "version":
                await stdout.WriteLineAsync(AgentConfig.Version);
                return 0;
            default:
                await stdout.WriteLineAsync(Usage);
                return args.Length == 0 ? 0 : 2;
        }
    }

    /// <summary>"cm-agent install [--stop-wait N] [--push on|off]": the values are saved, so the hooks written now and every later run agree on it.</summary>
    public static int Install(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr, string sourceBinary,
        Func<string, string[], int>? run = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);
        config = SavedSettings.Apply(config);
        if (Array.IndexOf(args, "--stop-wait") >= 0)
        {
            var text = Option(args, "--stop-wait") ?? "";
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > AgentConfig.WaitMaxSeconds)
            {
                stderr.WriteLine($"--stop-wait takes whole seconds from 0 to {AgentConfig.WaitMaxSeconds}, not \"{text}\".");
                return 2;
            }

            config = SavedSettings.SaveStopWait(config, seconds);
        }

        if (Array.IndexOf(args, "--push") >= 0)
        {
            var value = Option(args, "--push");
            if (value is not ("on" or "off"))
            {
                stderr.WriteLine($"--push takes on or off, not \"{value}\".");
                return 2;
            }

            config = SavedSettings.SavePush(config, value == "on");
        }

        return new PluginInstaller(config, stdout, run).Install(sourceBinary);
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

    public static async Task<int> StatusAsync(AgentConfig config, TextWriter stdout, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var identity = Identity.Load(config);
        using var store = new LocalStore(config.DatabasePath);
        using var probe = DaemonHost.TryLock(config.LockPath);
        await stdout.WriteLineAsync($"home: {config.Home}");
        await stdout.WriteLineAsync(identity.Connected ? $"connected: {identity.Server}" : "not connected (cm-agent login --server <url>)");
        await stdout.WriteLineAsync($"daemon: {(probe is null ? "running" : "not running")}");
        await stdout.WriteLineAsync($"events waiting: {store.OutboxCount()}");
        var (commands, soonest) = store.WaitingCommands((clock ?? TimeProvider.System).GetUtcNow());
        await stdout.WriteLineAsync(soonest is { } expires
            ? $"commands waiting: {commands} (oldest expires {expires.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)})"
            : "commands waiting: 0");
        await stdout.WriteLineAsync($"stop wait: {(int)config.StopWait.TotalSeconds} s");
        foreach (var line in PushStatus.Describe(config, store, (clock ?? TimeProvider.System).GetUtcNow(), null, probe is null)) await stdout.WriteLineAsync(line);
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
        var mcp = builder.Services.AddMcpServer(o =>
        {
            if (!config.PushEnabled) return; // off: the server is exactly what it was before the push existed
            o.Capabilities = new ServerCapabilities { Experimental = new Dictionary<string, object> { [ChannelHost.Capability] = new JsonObject() } };
            o.ServerInstructions = ChannelEnvelopes.Instructions;
        });
        mcp.WithStdioServerTransport().WithTools<MonitorTools>();
        if (config.PushEnabled) builder.Services.AddHostedService<ChannelHost>();
        await builder.Build().RunAsync();
    }
}
