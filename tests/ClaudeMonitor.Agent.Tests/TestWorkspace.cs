using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

internal static class TestWorkspace
{
    /// <summary>Stores a workspace switch the way the daemon's settings pass does: tagged with the workspace in agent.json.</summary>
    public static void Set(AgentConfig config, LocalStore store, string key, string value)
    {
        WorkspaceSettings.Tag(store, Id(config));
        store.Set(key, value);
    }

    /// <summary>The workspace in agent.json, written there first when there is none.</summary>
    public static Guid Id(AgentConfig config)
    {
        var identity = Identity.Peek(config) ?? new Identity(new string('k', 24));
        if (identity.WorkspaceId is { } id) return id;
        var workspace = Guid.NewGuid();
        (identity with { WorkspaceId = workspace }).Save(config);
        return workspace;
    }
}
