using System.Globalization;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

/// <summary>Whether the agent may update itself on its own: the machine's setting and the workspace's, the lower of the two. Unknown or unread is off.</summary>
public static class UpdatePolicy
{
    /// <summary>The workspace's cap, as the daemon last read it from the API.</summary>
    public const string WorkspaceKey = "settings.agent_update";

    public static string Effective(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        return UpdateModes.Lower(config.AutoUpdate, Daemon.WorkspaceSettings.Get(config, store, WorkspaceKey));
    }
}

/// <summary>What proves that a freshly started daemon works: it says which version it is and has been answered by the API since the update.</summary>
public static class UpdateHealth
{
    public const string VersionKey = "daemon.version";

    /// <summary>Failures of a heartbeat that say "the network", not "the API said no": nothing against the build that made them.</summary>
    public static readonly IReadOnlySet<string> NetworkErrors = new HashSet<string> { "HttpRequestException", "TimeoutException", "SocketException", "IOException" };

    public static bool IsHealthy(LocalStore store, string version, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Get(VersionKey) == version
               && DateTimeOffset.TryParse(store.Get(Daemon.Relay.LastContactKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var contact)
               && contact >= since;
    }
}
