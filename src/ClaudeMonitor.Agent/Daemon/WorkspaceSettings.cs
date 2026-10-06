using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The workspace's switches as the daemon last read them, tagged with the workspace agent.json named when that settings
/// pass began. A value tagged with another workspace than the one in agent.json now reads as unread (off): a settings pass
/// still in flight while `cm-agent login` moved the machine cannot carry the old workspace's consent into the new one.
/// </summary>
public static class WorkspaceSettings
{
    /// <summary>The workspace whose settings the stored switches are.</summary>
    public const string WorkspaceKey = "settings.workspace_id";

    /// <summary>What `cm-agent status` shows for a switch that is unread or belongs to another workspace.</summary>
    public const string Unread = "unread";

    /// <summary>Every workspace switch the daemon stores; login and logout forget them all.</summary>
    public static readonly IReadOnlyList<string> Switches =
        [ClaudeUpdate.ClaudePolicy.WorkspaceKey, Update.UpdatePolicy.WorkspaceKey, MachineMonitor.RemoteRunsKey, WorkspaceKey];

    public static void Tag(LocalStore store, Guid workspaceId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var (key, value) = TagEntry(workspaceId);
        store.Set(key, value);
    }

    /// <summary>The tag as a key/value pair, for a write that must carry it together with the values it vouches for.</summary>
    public static (string Key, string Value) TagEntry(Guid workspaceId) => (WorkspaceKey, workspaceId.ToString("D"));

    /// <summary>The stored value of a workspace switch, or null when it is unread or belongs to another workspace.</summary>
    public static string? Get(AgentConfig config, LocalStore store, string key)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        var from = store.Get(WorkspaceKey);
        return from is not null && Identity.Peek(config)?.WorkspaceId?.ToString("D") == from ? store.Get(key) : null;
    }
}
