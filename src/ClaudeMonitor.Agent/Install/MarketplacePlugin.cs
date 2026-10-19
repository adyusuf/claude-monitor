using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// The marketplace and plugin files the repository publishes (ADR-0007, part B): <c>.claude-plugin/marketplace.json</c> at the root and, under
/// <c>plugin/monitor-agent</c> and <c>plugin/aipod</c> (ADR-0013 section 1: the old plugin, now the bridge, and its replacement), the manifest, the hooks and the MCP server. They are rendered from <see cref="PluginInstaller.Hooks"/>, the one list of
/// hooks, and a test compares them with the committed files, so the two ways of installing cannot drift. The scripts, the skills and the release
/// pins beside them are written by hand.
/// </summary>
public static class MarketplacePlugin
{
    public const string PluginFolder = "plugin/monitor-agent";
    public const string AipodFolder = "plugin/aipod";
    public const string AipodPluginName = "aipod";
    public const string MarketplaceFile = ".claude-plugin/marketplace.json";
    public const string UserConfigServer = "server";

    /// <summary>The wrapper every hook and the MCP server go through: it runs the installed binary and is silent when there is none yet.</summary>
    public const string Wrapper = "${CLAUDE_PLUGIN_ROOT}/scripts/run.sh";
    public const string Bootstrap = "${CLAUDE_PLUGIN_ROOT}/scripts/ensure.sh";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The timeout of a hook (the plugin's hooks are fixed): the permission wait of 120 s plus a margin, and for Stop the longest wait a person can set
    /// (<see cref="AgentConfig.WaitMaxSeconds"/>) plus a margin, so `aipod install --stop-wait` works for a plugin-started agent too; a Stop hook waits only as long
    /// as the saved wait, zero by default.
    /// </summary>
    public static int Timeout(string hook, int declared) => declared > 0 ? declared : hook switch
    {
        "PermissionRequest" => 120 + 15,
        "Stop" => AgentConfig.WaitMaxSeconds + 10,
        _ => 15,
    };

    /// <summary>Every file as (path relative to the repository root, content).</summary>
    public static IReadOnlyDictionary<string, string> Render(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        var files = new Dictionary<string, string> { [MarketplaceFile] = Text(Marketplace()) };
        // Both plugins run the same hooks and the same MCP server through their own wrapper; only the name and the words differ.
        foreach (var (folder, name, description, brand) in new[]
        {
            (PluginFolder, AgentConfig.PluginName, OldDescription, "Claude Monitor"),
            (AipodFolder, AipodPluginName, "aipod agent: installs itself, connects to your server, reports sessions and delivers web messages.", "aipod"),
        })
        {
            files[$"{folder}/.claude-plugin/plugin.json"] = Text(Manifest(name, version, description, brand, serverRequired: name != AipodPluginName));
            files[$"{folder}/hooks/hooks.json"] = Text(new JsonObject { ["hooks"] = Hooks() });
            files[$"{folder}/.mcp.json"] = Text(Mcp());
        }

        return files;
    }

    private const string OldDescription = "Replaced by the aipod plugin (run /aipod:connect). Claude Monitor agent: installs itself, connects to your server, reports sessions and delivers web messages.";

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
        ["plugins"] = new JsonArray(
            new JsonObject
            {
                ["name"] = AgentConfig.PluginName,
                ["source"] = "./" + PluginFolder,
                ["description"] = "The old plugin of the aipod agent, kept as a bridge: install aipod instead.",
            },
            new JsonObject
            {
                ["name"] = AipodPluginName,
                ["source"] = "./" + AipodFolder,
                ["description"] = "Installs the aipod agent, connects it to your server and starts it with your Claude Code sessions.",
            }),
    };

    /// <summary>The server option is required for the old plugin; the aipod one asks only when the agent has no server of its own to take (a machine that is already connected is not asked again).</summary>
    private static JsonObject Manifest(string name, string version, string description, string brand, bool serverRequired) => new()
    {
        ["name"] = name,
        ["version"] = version,
        ["description"] = description,
        ["author"] = new JsonObject { ["name"] = brand },
        ["userConfig"] = new JsonObject
        {
            [UserConfigServer] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = "Server address",
                ["description"] = $"The https address of your {brand} server: the address you open in the browser.",
                ["required"] = serverRequired,
            },
            ["push"] = new JsonObject
            {
                ["type"] = "string",
                ["title"] = "Push web messages into idle sessions",
                ["description"] = "Type inbox to start a turn in an idle session when a message is sent from the web. Leave empty: a message then arrives at the session's next prompt.",
                ["required"] = false,
            },
        },
    };

    private static JsonObject Hooks()
    {
        var hooks = new JsonObject();
        foreach (var (name, isAsync, declared, extra) in PluginInstaller.Hooks)
        {
            if (hooks[name] is not JsonArray entries)
            {
                var group = new JsonArray();
                if (name == "SessionStart") group.Add(Handler($"sh \"{Bootstrap}\"", 10, isAsync: false)); // installs the binary on the first session
                entries = new JsonArray(new JsonObject { ["hooks"] = group });
                hooks[name] = entries;
            }

            var command = $"sh \"{Wrapper}\" hook {name}" + (extra is null ? "" : " " + extra);
            entries[0]!["hooks"]!.AsArray().Add(Handler(command, Timeout(name, declared), isAsync));
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
