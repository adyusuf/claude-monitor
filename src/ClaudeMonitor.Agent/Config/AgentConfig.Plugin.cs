namespace ClaudeMonitor.Agent.Config;

/// <summary>What the agent knows when Claude Code runs it from the marketplace plugin (ADR-0007, part B). All null in every other way of starting it.</summary>
public sealed partial record AgentConfig
{
    /// <summary>The marketplace the repository publishes the plugin in (the other one, <see cref="MarketplaceName"/>, is the local copy `cm-agent install` writes).</summary>
    public const string ReleasesMarketplaceName = "monitor-agent-releases";

    /// <summary>
    /// The binary to run and to update (CM_BINARY), set by the plugin's wrapper script to the copy in its data directory. Null: the copy in
    /// the agent's home, where `cm-agent install` puts it.
    /// </summary>
    public string? BinaryOverride { get; init; }

    /// <summary>Which plugin started this process ("name@marketplace", CM_PLUGIN_ID): the daemon stops when Claude Code no longer enables it.</summary>
    public string? PluginId { get; init; }

    /// <summary>
    /// The plugin's data directory (CLAUDE_PLUGIN_DATA, exported by Claude Code to its hooks and MCP servers): kept across plugin updates and
    /// deleted when the plugin is uninstalled, which is the only uninstall signal Claude Code gives.
    /// </summary>
    public string? PluginDataDir { get; init; }

    /// <summary>How often the daemon looks at whether its plugin is still installed and enabled.</summary>
    public TimeSpan PluginWatchEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long `cm-agent connect` waits for the login process to show its code before it says where to look.</summary>
    public TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A code shown less than this long ago is shown again instead of starting a second login.</summary>
    public TimeSpan ConnectReuse { get; init; } = TimeSpan.FromMinutes(10);

    public string ConnectLogPath => Path.Combine(Home, "connect.log");
}
