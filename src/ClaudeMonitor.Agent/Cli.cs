using System.Globalization;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Push;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeMonitor.Agent;

/// <summary>The command line. Each command is a thin call into its own class.</summary>
public static partial class Cli
{
    public const string Usage = """
        aipod login --server <url>   connect this machine (shows a code to approve on the web)
        aipod install [--stop-wait <seconds>] [--push off|inbox|channel]
                                        register the Claude Code plugin (hooks + MCP) that starts the agent;
                                        --stop-wait: how long a finished turn waits for a prompt from the web (0-590, default 0)
                                        --push: deliver web messages into an idle session (default off). inbox: the daemon writes to the
                                        session's own socket, no start-up flag (ADR-0007); channel (also "on"): a Claude Code channel (ADR-0003)
        aipod install --exec off|argv|shell
                                        whether approved remote runs may execute on this machine (default off)
        aipod install --standalone [--server <url>] [--exec off|argv|shell]
        aipod uninstall --standalone
                                        this machine as a daemon without Claude Code: it logs in first, then starts when you log in
                                        (an application on macOS and Windows, systemd --user on Linux), in your own account (ADR-0008)
        aipod install --service --server <url> [--exec off|argv|shell] [--allow-root] [--run-account [<name>]]
        aipod uninstall --service    (admin) a boot service under its own account, for servers (ADR-0005); --run-account makes remote runs start
                                        as a second unprivileged account (Linux and macOS, through sudo; ADR-0010)
        aipod connect [--server <url>]
                                        like login, from inside Claude Code: prints the code and returns; the login finishes in the background
        aipod restart                hand the running daemon over to this binary
        aipod status                 connection and queue
        aipod update [--check]       install the newest signed agent build from the server (--check: only look)
        aipod config [auto-update off|check|on] [claude-update on|off] [push off|inbox|channel]
                                        whether the agent looks for / installs agent updates by itself, and whether it may
                                        update Claude Code when every session is idle (both default off; the workspace must allow too)
        aipod config projects add|remove|list [<folder>]  the project folders this machine reports facts about (ADR-0008)
        aipod config role <environment|none> <function|none>
                                        what this machine says it is (dev|test|stage|preprod|production|other; development|application|web|
                                        database|ci|storage|other); an admin sets the role on the web, a claim only makes it stricter (ADR-0008)
        aipod approvers add <public key> [--name <n>] | remove <fingerprint> | list
        aipod config approvals required|off
                                        pin an owner's approval key (copy it from the web; compare the fingerprint) and require that every remote run is
                                        signed by a pinned key; off by default. An admin runs these (the file is admin-owned on a service) (ADR-0010)
        aipod machines | machine <x> | metrics <x> | alerts [<x>] | jobs [<x>] | job-run <job id>
        aipod run --machine <x> (--script <file> --interpreter <i> [--arg <v>]... | --argv <exe> [args...]) [--timeout] [--reason] [--project] [--kind]
        aipod result <run or ask id> [--wait <seconds>] [--offset <chars>]
        aipod ask --machine <x> "<question>"
                                        reach the other machines of the workspace from here, without the web (an approval is still the owner's, on the web; ADR-0008)
        aipod claude-update cancel   stop a Claude Code update that is counting down
        aipod logout | uninstall | version
        (aipod hook <Event> | mcp | daemon are started by Claude Code and the agent itself)
        """;

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextReader stdin, TextWriter stdout, TextWriter stderr,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(args);
        // The names of the tools for this run (before anything names one): --names, else AIPOD_TOOL_NAMES, else the old ones. Scoped to this call, so
        // a test that runs the command line in this process does not change what a parallel test sees.
        using var toolNames = McpToolNames.Scope(Option(args, "--names") is { } names ? McpToolNames.Parse(names) : config.ToolNamesMode);
        // A boot service is installed by an admin: nothing may be written to the admin's own home first (ADR-0005).
        if (args.FirstOrDefault() == "install" && ServiceCommands.IsService(args)) return ServiceCommands.Install(args, stdout, stderr);
        if (args.FirstOrDefault() == "uninstall" && ServiceCommands.IsService(args)) return ServiceCommands.Uninstall(stdout);
        if (args.FirstOrDefault() == "install" && StandaloneCommands.IsStandalone(args)) return await StandaloneCommands.InstallAsync(args, config, stdout, stderr, clock);
        if (args.FirstOrDefault() == "uninstall" && StandaloneCommands.IsStandalone(args)) return StandaloneCommands.Uninstall(config, stdout);
        var log = new AgentLog(config, clock);
        HomeMigration.Run(config, log);
        config = SavedSettings.Apply(config);
        switch (args.FirstOrDefault())
        {
            case "hook" when args.Length > 2 && args[1] == SessionContext.HookEvent && args[2] == SessionContext.ContextOption:
                return await SessionContext.RunAsync(config, stdout, stderr, clock, stdin); // the machines block and the prompts waiting for this session, no network, exit 0 (ADR-0008)
            case "hook" when args.Length > 1:
                return await HookAsync(args[1], config, stdin, stdout, stderr, clock, log);
            case "mcp":
                DaemonHost.EnsureRunning(config, log);
                await McpAsync(config, clock);
                return 0;
            case "daemon":
                return await DaemonRole.RunAsync(config, clock, log);

            case "login":
                var server = Option(args, "--server");
                if (Array.IndexOf(args, "--detach") >= 0) DaemonHost.Detach(); // started by `connect`: outlive the session that asked
                await using (var file = Option(args, "--output") is { } target ? new StreamWriter(target) { AutoFlush = true } : null)
                {
                    var code = await new Login(config, file ?? stdout, clock).RunAsync(server, null, CancellationToken.None);
                    if (code == 0) DaemonHost.EnsureRunning(config, log);
                    return code;
                }

            case "restart":
                return await RestartAsync(config, log, clock, stdout);
            case "connect":
                return await new ConnectCommand(config, stdout, clock, new DetachedProcess()).RunAsync(Option(args, "--server"), Environment.ProcessPath!, CancellationToken.None);
            case "plugin-files" when args.Length > 1:
                stdout.WriteLine($"{MarketplacePlugin.WriteTo(args[1], AgentConfig.Version)} files written under {args[1]}");
                return 0;
            case "logout":
                return await new Login(config, stdout, clock).LogoutAsync();
            case var verb when verb is not null && RemoteVerbs.Names.Contains(verb):
                return await RemoteVerbs.RunAsync(args, config, stdout, stderr, clock, log);
            case "install" when Array.IndexOf(args, ServiceCommands.ExecOption) >= 0:
                var saved = ServiceCommands.SaveExecLevel(args, config, stdout, stderr);
                return saved != 0 || args.Length == 3 ? saved : Install(args, config, stdout, stderr, Environment.ProcessPath!);
            case "install":
                return Install(args, config, stdout, stderr, Environment.ProcessPath!);
            case "uninstall":
                return new PluginInstaller(config, stdout).Uninstall();
            case "update":
                return await UpdateCommand.UpdateAsync(args, config, stdout, stderr, clock);
            case ClaudeUpdateCommand.Verb:
                return await ClaudeUpdateCommand.RunAsync(args, config, stdout, stderr, clock);
            case "config":
                return await UpdateCommand.ConfigAsync(args, config, stdout, stderr);
            case "approvers":
                return await Approvals.ApproversCommand.RunAsync(args, config, stdout, stderr);
            case "status":
                return await StatusAsync(config, stdout, clock);
            case "statusline":
                return await StatusLine.StatusLineCommand.RunAsync(config, stdin, stdout, stderr, clock); // Claude Code runs this; it never fails it
            case "version":
                await stdout.WriteLineAsync(AgentConfig.Version);
                return 0;
            default:
                await stdout.WriteLineAsync(Usage);
                return args.Length == 0 ? 0 : 2;
        }
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
            if (config.InboxShortEnabled) o.ServerInstructions = McpToolNames.Text(ChannelEnvelopes.InboxInstructions); // the one-line notice needs the rule for reading it
            if (!config.ChannelPushEnabled) return; // off, or through the inbox: no channel
            o.Capabilities = new ServerCapabilities { Experimental = new Dictionary<string, object> { [ChannelHost.Capability] = new JsonObject() } };
            o.ServerInstructions = McpToolNames.Text(ChannelEnvelopes.Instructions);
        });
        mcp.WithStdioServerTransport().AddTools(config, clock, McpToolNames.Mode);
        if (config.ChannelPushEnabled) builder.Services.AddHostedService<ChannelHost>();
        await builder.Build().RunAsync();
    }
}
