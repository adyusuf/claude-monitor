using System.Text.Json;

namespace ClaudeMonitor.Api.Data;

// Sessions, what they report, commands and audit (docs/data-model.md §4-6).

public static class SessionStatuses
{
    public const string Active = "active";
    public const string Idle = "idle";
    public const string Waiting = "waiting";
    public const string Ended = "ended";
}

public static class PermissionStatuses
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string Expired = "expired";
}

public sealed class HarnessKind
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DisplayNameSearch { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class HarnessSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? ProjectId { get; set; }
    public string HarnessKind { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string? Title { get; set; }
    public string TitleSearch { get; set; } = "";
    public string? Model { get; set; }
    public string? GitBranch { get; set; }
    public string Status { get; set; } = SessionStatuses.Active;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class SessionTask
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public string ExternalId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string SubjectSearch { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SubagentRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public string ExternalId { get; set; } = "";
    public string AgentType { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class SessionUsage
{
    public Guid SessionId { get; set; }
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AgentBatch
{
    public Guid AgentId { get; set; }
    public long BatchSeq { get; set; }
    public int EventCount { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class SessionEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public JsonDocument Payload { get; set; } = JsonDocument.Parse("{}");
    public bool Truncated { get; set; }
}

public sealed class EventArchive
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public DateOnly Day { get; set; }
    public string Path { get; set; } = "";
    public int EventCount { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SessionCommand
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AgentId { get; set; }
    public string Kind { get; set; } = "";
    public string? Body { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = "queued";
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public string? Result { get; set; }
}

public sealed class PermissionRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AgentId { get; set; }
    public string ToolName { get; set; } = "";
    public JsonDocument ToolInput { get; set; } = JsonDocument.Parse("{}");
    public string Status { get; set; } = PermissionStatuses.Open;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Decision { get; set; }
    public string? Reason { get; set; }
    public Guid? AnsweredBy { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ActorAgentId { get; set; }
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public DateTimeOffset At { get; set; }
    public string? IpHash { get; set; }
    public JsonDocument? Detail { get; set; }
}
