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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
