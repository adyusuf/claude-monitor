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

    /// <summary>
    /// The longest Stop-hook wait the plugin's hook timeout allows (CM_PLUGIN_STOP_WAIT_MAX, set by the plugin's wrapper script of a version whose
    /// hooks have the long timeout; zero when it is not set, as for an older plugin version, whose fixed 15 s would cut a longer wait off and could
    /// lose a prompt taken just before). The saved stop wait (`cm-agent install --stop-wait`) is held to it for a plugin-started agent.
    /// </summary>
    public TimeSpan PluginStopWaitMax { get; init; } = TimeSpan.Zero;

    /// <summary>How often the daemon looks at whether its plugin is still installed and enabled.</summary>
    public TimeSpan PluginWatchEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long `cm-agent connect` waits for the login process to show its code before it says where to look.</summary>
    public TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A code shown less than this long ago is shown again instead of starting a second login.</summary>
    public TimeSpan ConnectReuse { get; init; } = TimeSpan.FromMinutes(10);

    public string ConnectLogPath => Path.Combine(Home, "connect.log");

    /// <summary>The server the login in <see cref="ConnectLogPath"/> was started for (the login truncates the log itself, so it cannot hold this).</summary>
    public string ConnectServerPath => ConnectLogPath + ".server";
}
