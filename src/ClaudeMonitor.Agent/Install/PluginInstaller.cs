using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// "cm-agent install": copies the binary to a stable place in the agent's home and registers a Claude Code plugin
/// (a local marketplace with one plugin) whose hooks and MCP server run that binary. Claude Code then starts the
/// agent itself: the first hook or MCP call wakes the daemon (ADR-0002). Nothing is registered with the OS.
/// </summary>
public sealed class PluginInstaller(AgentConfig config, TextWriter output, Func<string, string[], int>? run = null)
{
    /// <summary>
    /// The hooks: observational ones run asynchronously, the ones that can carry an answer synchronously. An event may have more than one
    /// entry: SessionStart also runs the synchronous `--context` entry that hands the session the machines block (ADR-0008 §3b).
    /// </summary>
    public static readonly (string Event, bool Async, int Timeout, string? Args)[] Hooks =
    [
        ("SessionStart", true, 10, null),
        ("SessionStart", false, 5, Capture.SessionContext.ContextOption),
        ("UserPromptSubmit", false, 10, null),
        ("PreToolUse", false, 10, null),
        ("PostToolUse", true, 10, null),
        ("PostToolUseFailure", true, 10, null),
        ("PermissionRequest", false, 0, null),
        ("Notification", true, 10, null),
        ("Stop", false, 0, null),
        ("SubagentStart", true, 10, null),
        ("SubagentStop", true, 10, null),
        ("SessionEnd", false, 5, null),
    ];

    public const string McpServerName = "claude-monitor";

    /// <summary>How Claude Code must be started for the push to arrive: a channel is research-preview and not on the allowlist (ADR-0003).</summary>
    public static readonly string ChannelFlag = $"--dangerously-load-development-channels plugin:{AgentConfig.PluginName}@{AgentConfig.MarketplaceName}";

    public string BinaryPath => config.BinaryPath;

    public int Install(string sourceBinary)
    {
        config.EnsureHome();
        Directory.CreateDirectory(Path.GetDirectoryName(BinaryPath)!);
        if (!string.Equals(Path.GetFullPath(sourceBinary), Path.GetFullPath(BinaryPath), StringComparison.Ordinal))
        {
            File.Copy(sourceBinary, BinaryPath, overwrite: true);
        }

        WritePlugin();
        var claude = run ?? Run;
        var added = claude("claude", ["plugin", "marketplace", "add", config.PluginDir]);
        var installed = added == 0 ? claude("claude", ["plugin", "install", $"{AgentConfig.PluginName}@{AgentConfig.MarketplaceName}", "--scope", "user"]) : added;
        if (installed != 0)
        {
            output.WriteLine("Claude Code could not be reached. Register the plugin yourself:");
            output.WriteLine($"  claude plugin marketplace add \"{config.PluginDir}\"");
            output.WriteLine($"  claude plugin install {AgentConfig.PluginName}@{AgentConfig.MarketplaceName} --scope user");
            return 1;
        }

        output.WriteLine($"Installed. Claude Code starts the agent with its sessions. Binary: {BinaryPath}");
        output.WriteLine(config.StopWait > TimeSpan.Zero
            ? $"A finished turn waits up to {(int)config.StopWait.TotalSeconds} s for a prompt from the web."
            : "A finished turn does not wait for the web (cm-agent install --stop-wait <seconds> changes that).");
        output.WriteLine(config.ChannelPushEnabled
            ? $"Push is ON through a channel. Start Claude Code with:  claude {ChannelFlag}  (a channel is a research preview; the agent cannot enable it for you)."
            : config.InboxPushEnabled
                ? "Push is ON through each session's inbox: a web message starts a turn in an idle session, with no start-up flag (ADR-0007)."
                : "Web messages reach a session at its next prompt (cm-agent install --push inbox delivers them into an idle session).");
        return 0;
    }

    public int Uninstall()
    {
        var claude = run ?? Run;
        claude("claude", ["plugin", "uninstall", $"{AgentConfig.PluginName}@{AgentConfig.MarketplaceName}"]);
        claude("claude", ["plugin", "marketplace", "remove", AgentConfig.MarketplaceName]);
        output.WriteLine("The Claude Code plugin is removed. The agent's data stays in " + config.Home);
        return 0;
    }

    /// <summary>The local marketplace: .claude-plugin/marketplace.json and the plugin folder with hooks and MCP.</summary>
    public void WritePlugin()
    {
        var root = config.PluginDir;
        var plugin = Path.Combine(root, AgentConfig.PluginName);
        Write(Path.Combine(root, ".claude-plugin", "marketplace.json"), new JsonObject
        {
            ["name"] = AgentConfig.MarketplaceName,
            ["owner"] = new JsonObject { ["name"] = "Claude Monitor" },
            ["description"] = "The Claude Monitor agent installed on this machine (one plugin: hooks and an MCP server).",
            ["plugins"] = new JsonArray(new JsonObject
            {
                ["name"] = AgentConfig.PluginName,
                ["source"] = "./" + AgentConfig.PluginName,
                ["description"] = "Reports this machine's sessions to Claude Monitor and carries its commands back.",
            }),
        });
        var manifest = new JsonObject
        {
            ["name"] = AgentConfig.PluginName,
            ["version"] = AgentConfig.Version,
            ["description"] = "Claude Monitor agent: hooks and an MCP server that run the installed cm-agent.",
            ["author"] = new JsonObject { ["name"] = "Claude Monitor" },
        };
        // Push on: the MCP server is also a channel (ADR-0003). Without the entry (the default) the plugin is what it was.
        if (config.ChannelPushEnabled) manifest["channels"] = new JsonArray(new JsonObject { ["server"] = McpServerName, ["displayName"] = "Claude Monitor" });
        Write(Path.Combine(plugin, ".claude-plugin", "plugin.json"), manifest);
        var hooks = new JsonObject();
        foreach (var (name, isAsync, timeout, extra) in Hooks)
        {
            var args = new JsonArray("hook", name);
            if (extra is not null) args.Add(extra);
            var handler = new JsonObject
            {
                ["type"] = "command",
                ["command"] = BinaryPath,
                ["args"] = args,
                ["timeout"] = timeout > 0 ? timeout : name == "PermissionRequest"
                    ? (int)config.PermissionWait.TotalSeconds + 15
                    : (int)config.StopWait.TotalSeconds + 15,
            };
            if (isAsync) handler["async"] = true;
            if (hooks[name] is JsonArray existing) existing[0]!["hooks"]!.AsArray().Add(handler);
            else hooks[name] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(handler) });
        }

        Write(Path.Combine(plugin, "hooks", "hooks.json"), new JsonObject { ["hooks"] = hooks });
        Write(Path.Combine(plugin, ".mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpServerName] = new JsonObject { ["command"] = BinaryPath, ["args"] = new JsonArray("mcp") },
            },
        });
    }

    private static void Write(string path, JsonNode content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int Run(string tool, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 127; // claude is not on PATH
        }
    }
}
