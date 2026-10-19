using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Data;

/// <summary>
/// One fixed action (restart, update check, update, status) its owner asked a machine's agent to perform (ADR-0005, "Agent actions
/// from the web"). <see cref="Result"/> is the agent's own short text, capped; it is data and is shown as text only.
/// </summary>
public sealed class AgentAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid AgentId { get; set; }
    public Guid RequestedBy { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = AgentActionStatuses.Queued;
    public string? Result { get; set; }

    /// <summary>Claude control (ADR-0014): the project key and permission mode of a start, and the short session id a start returned or a stop names. NULL for the other kinds.</summary>
    public string? ProjectKey { get; set; }
    public string? PermissionMode { get; set; }
    public string? SessionRef { get; set; }

    /// <summary>ADR-0014 P2: the text of a start's first prompt, kept only until the agent has taken the action (then NULL); never audited, never shown back.</summary>
    public string? FirstPrompt { get; set; }

    /// <summary>A remote write (ADR-0011 §7a): the plugin id or the setting name, and the value of a setting. Closed shapes (<see cref="ClaudeConfigRules"/>), never free text; NULL for the other kinds.</summary>
    public string? Target { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
