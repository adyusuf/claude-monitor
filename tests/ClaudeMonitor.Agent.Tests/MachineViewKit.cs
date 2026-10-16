using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Machines as the API serves them, for the tests of the session-start block and its cache.</summary>
public static class MachineViewKit
{
    public static MachineView View(string host, Guid? id = null, string? effectiveEnvironment = null, string? effectiveFunction = null,
        string? claimedEnvironment = null, string? claimedFunction = null, string? claudeKind = null, string? claudeVersion = null,
        int? idle = null, IReadOnlyList<ToolInfo>? tools = null, IReadOnlyList<ToolInfo>? interpreters = null, bool online = true) =>
        new(id ?? Guid.NewGuid(), host, "linux", "someone", ExecLevels.Argv, ServiceMode: false, online, null, null, 0,
            EffectiveEnvironment: effectiveEnvironment, EffectiveFunction: effectiveFunction, ClaimedEnvironment: claimedEnvironment,
            ClaimedFunction: claimedFunction, ClaudeKind: claudeKind, ClaudeVersion: claudeVersion, ClaudeSessionsIdle: idle,
            Tools: tools, Interpreters: interpreters);

    public static ToolInfo Tool(string name) => new(name, "/usr/bin/" + name, "1.0", true);
}
