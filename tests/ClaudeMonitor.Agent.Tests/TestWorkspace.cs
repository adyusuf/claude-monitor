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
        var identity = Identity.Peek(config) ?? new Identity(new string('k', 24));
        var workspace = identity.WorkspaceId ?? Guid.NewGuid();
        if (identity.WorkspaceId is null) (identity with { WorkspaceId = workspace }).Save(config);
        WorkspaceSettings.Tag(store, workspace);
        store.Set(key, value);
    }
}
