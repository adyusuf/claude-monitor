using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Podium.Agent.Config;

namespace Podium.Agent.Install;

/// <summary>
/// "aipod install": copies the binary to a stable place in the agent's home and registers a Claude Code plugin
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

    public const string McpServerName = "aipod";

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
            output.WriteLine(Strings.T(Msg.PluginNoClaude));
            output.WriteLine($"  claude plugin marketplace add \"{config.PluginDir}\"");
            output.WriteLine($"  claude plugin install {AgentConfig.PluginName}@{AgentConfig.MarketplaceName} --scope user");
            return 1;
        }

        output.WriteLine(Strings.T(Msg.PluginInstalled, BinaryPath));
        output.WriteLine(config.StopWait > TimeSpan.Zero
            ? Strings.T(Msg.PluginWaits, (int)config.StopWait.TotalSeconds)
            : Strings.T(Msg.PluginNoWait));
        output.WriteLine(config.ChannelPushEnabled
            ? Strings.T(Msg.PluginPushChannel, ChannelFlag)
            : config.InboxPushEnabled
                ? Strings.T(Msg.PluginPushInbox)
                : Strings.T(Msg.PluginPushOff));
        return 0;
    }

    public int Uninstall()
    {
        var claude = run ?? Run;
        claude("claude", ["plugin", "uninstall", $"{AgentConfig.PluginName}@{AgentConfig.MarketplaceName}"]);
        claude("claude", ["plugin", "marketplace", "remove", AgentConfig.MarketplaceName]);
        output.WriteLine(Strings.T(Msg.PluginRemoved, config.Home));
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
            ["owner"] = new JsonObject { ["name"] = "Podium" },
            ["description"] = "The Podium agent installed on this machine (one plugin: hooks and an MCP server).",
            ["plugins"] = new JsonArray(new JsonObject
            {
                ["name"] = AgentConfig.PluginName,
                ["source"] = "./" + AgentConfig.PluginName,
                ["description"] = "Reports this machine's sessions to Podium and carries its commands back.",
            }),
        });
        var manifest = new JsonObject
        {
            ["name"] = AgentConfig.PluginName,
            ["version"] = AgentConfig.Version,
            ["description"] = "Podium agent: hooks and an MCP server that run the installed aipod.",
            ["author"] = new JsonObject { ["name"] = "Podium" },
        };
        // Push on: the MCP server is also a channel (ADR-0003). Without the entry (the default) the plugin is what it was.
        if (config.ChannelPushEnabled) manifest["channels"] = new JsonArray(new JsonObject { ["server"] = McpServerName, ["displayName"] = "Podium" });
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
