using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// The marketplace and plugin files the repository publishes (ADR-0007, part B): <c>.claude-plugin/marketplace.json</c> at the root and, under
/// <c>plugin/monitor-agent</c>, the manifest, the hooks and the MCP server. They are rendered from <see cref="PluginInstaller.Hooks"/>, the one list of
/// hooks, and a test compares them with the committed files, so the two ways of installing cannot drift. The scripts, the skills and the release
/// pins beside them are written by hand.
/// </summary>
public static class MarketplacePlugin
{
    public const string PluginFolder = "plugin/monitor-agent";
    public const string MarketplaceFile = ".claude-plugin/marketplace.json";
    public const string UserConfigServer = "server";

    /// <summary>The wrapper every hook and the MCP server go through: it runs the installed binary and is silent when there is none yet.</summary>
    public const string Wrapper = "${CLAUDE_PLUGIN_ROOT}/scripts/run.sh";
    public const string Bootstrap = "${CLAUDE_PLUGIN_ROOT}/scripts/ensure.sh";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Claude Code's own limit for a hook, which the agent's waits stay under: a prompt wait of 120 s and no stop wait by default.</summary>
    public static int Timeout(string hook, int declared) => declared > 0 ? declared : hook == "PermissionRequest" ? 120 + 15 : 0 + 15;

    /// <summary>Every file as (path relative to the repository root, content).</summary>
    public static IReadOnlyDictionary<string, string> Render(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        var folder = PluginFolder;
        return new Dictionary<string, string>
        {
            [MarketplaceFile] = Text(Marketplace()),
            [$"{folder}/.claude-plugin/plugin.json"] = Text(Manifest(version)),
            [$"{folder}/hooks/hooks.json"] = Text(new JsonObject { ["hooks"] = Hooks() }),
            [$"{folder}/.mcp.json"] = Text(Mcp()),
        };
    }

    /// <summary>Writes the rendered files under <paramref name="root"/>. Returns how many.</summary>
    public static int WriteTo(string root, string version)
    {
        var files = Render(version);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return files.Count;
    }

    private static string Text(JsonNode node) => node.ToJsonString(Format) + "\n";

    private static JsonObject Marketplace() => new()
    {
        ["name"] = AgentConfig.ReleasesMarketplaceName,
        ["owner"] = new JsonObject { ["name"] = "Claude Monitor" },
        ["description"] = "The Claude Monitor agent as a Claude Code plugin: it installs, connects and stops the agent, which reports this machine's sessions and carries web messages back.",
        ["plugins"] = new JsonArray(new JsonObject
        {
            ["name"] = AgentConfig.PluginName,
            ["source"] = "./" + PluginFolder,
            ["description"] = "Installs the Claude Monitor agent, connects it to your server and starts it with your Claude Code sessions.",
        }),
    };

    private static JsonObject Manifest(string version) => new()
    {
        ["name"] = AgentConfig.PluginName,
        ["version"] = version,
        ["description"] = "Claude Monitor agent: installs itself, connects to your server, reports sessions and delivers web messages.",
        ["author"] = new JsonObject { ["name"] = "Claude Monitor" },
        ["userConfig"] = new JsonObject
        {
            [UserConfigServer] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = "Server address",
                ["description"] = "The https address of your Claude Monitor server: the address you open in the browser.",
                ["required"] = true,
            },
        },
    };

    private static JsonObject Hooks()
    {
        var hooks = new JsonObject();
        foreach (var (name, isAsync, declared) in PluginInstaller.Hooks)
        {
            var group = new JsonArray();
            if (name == "SessionStart") group.Add(Handler($"sh \"{Bootstrap}\"", 10, isAsync: false)); // installs the binary on the first session
            group.Add(Handler($"sh \"{Wrapper}\" hook {name}", Timeout(name, declared), isAsync));
            hooks[name] = new JsonArray(new JsonObject { ["hooks"] = group });
        }

        return hooks;
    }

    private static JsonObject Handler(string command, int timeout, bool isAsync)
    {
        var handler = new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = timeout };
        if (isAsync) handler["async"] = true;
        return handler;
    }

    private static JsonObject Mcp() => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [PluginInstaller.McpServerName] = new JsonObject
            {
                ["command"] = "sh",
                ["args"] = new JsonArray($"{Wrapper}", "mcp"),
                ["env"] = new JsonObject { ["CM_SERVER"] = "${user_config." + UserConfigServer + "}" },
            },
        },
    };
}
