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
    /// <summary>The hooks: observational ones run asynchronously, the ones that can carry an answer synchronously.</summary>
    public static readonly (string Event, bool Async, int Timeout)[] Hooks =
    [
        ("SessionStart", true, 10),
        ("UserPromptSubmit", false, 10),
        ("PreToolUse", false, 10),
        ("PostToolUse", true, 10),
        ("PostToolUseFailure", true, 10),
        ("PermissionRequest", false, 0),
        ("Notification", true, 10),
        ("Stop", false, 0),
        ("SubagentStart", true, 10),
        ("SubagentStop", true, 10),
        ("SessionEnd", false, 5),
    ];

    public string BinaryPath => Path.Combine(config.Home, "bin", OperatingSystem.IsWindows() ? "cm-agent.exe" : "cm-agent");

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
        Write(Path.Combine(plugin, ".claude-plugin", "plugin.json"), new JsonObject
        {
            ["name"] = AgentConfig.PluginName,
            ["version"] = AgentConfig.Version,
            ["description"] = "Claude Monitor agent: hooks and an MCP server that run the installed cm-agent.",
            ["author"] = new JsonObject { ["name"] = "Claude Monitor" },
        });
        var hooks = new JsonObject();
        foreach (var (name, isAsync, timeout) in Hooks)
        {
            var handler = new JsonObject
            {
                ["type"] = "command",
                ["command"] = BinaryPath,
                ["args"] = new JsonArray("hook", name),
                ["timeout"] = timeout > 0 ? timeout : name == "PermissionRequest"
                    ? (int)config.PermissionWait.TotalSeconds + 15
                    : (int)config.StopWait.TotalSeconds + 15,
            };
            if (isAsync) handler["async"] = true;
            hooks[name] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(handler) });
        }

        Write(Path.Combine(plugin, "hooks", "hooks.json"), new JsonObject { ["hooks"] = hooks });
        Write(Path.Combine(plugin, ".mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["claude-monitor"] = new JsonObject { ["command"] = BinaryPath, ["args"] = new JsonArray("mcp") },
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
