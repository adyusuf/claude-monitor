using System.Text.Json;

namespace Podium.Api.Data;

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

public sealed class Repo
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
    public Guid? RepoId { get; set; }
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

    /// <summary>The session's last event time when the owner took it off the lists (the agent's clock); a later event shows it again. Null = shown.</summary>
    public DateTimeOffset? HiddenAt { get; set; }
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

    /// <summary>How the command reached its session: <c>hook</c>, <c>inbox</c> or <c>channel</c> (ADR-0007); null for an older agent or before it was applied.</summary>
    public string? Transport { get; set; }
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

    /// <summary>The approval rule that allowed the request at once (ADR-0019); null when a person answered or nobody did.</summary>
    public Guid? AnsweredByRule { get; set; }

    /// <summary>
    /// The session's project (repository) when the request was made: a rule made from this request holds this one, the project the request
    /// was matched in, even when the session has moved to another since (ADR-0019). Null: none, or a request from before this column.
    /// </summary>
    public Guid? RepoId { get; set; }
}

/// <summary>
/// An owner's standing allow for one tool on one machine in one project (ADR-0019): for <c>Bash</c> one exact command, for a file or reading
/// tool one exact path, for a search tool one exact search. Only the tools of <see cref="Approvals.ApprovalRules"/> can have one.
/// Ends at <see cref="ExpiresAt"/> (null: always) or when revoked; never made or matched on a strict machine.
/// </summary>
public sealed class ApprovalRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid AgentId { get; set; }
    /// <summary>The project (repository) of the session the rule was made in; it acts only in sessions of that project (null: none).</summary>
    public Guid? RepoId { get; set; }
    public string ToolName { get; set; } = "";
    /// <summary>The value the rule pins: the exact command for <c>Bash</c>, the exact path for a file or reading tool, the exact search for a search tool.</summary>
    public string? Command { get; set; }
    public string Duration { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid CreatedFromRequest { get; set; }
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
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

/// <summary>The limits of approval rules (ADR-0019).</summary>
public static class ApprovalRuleLimits
{
    /// <summary>A longer <c>Bash</c> command can be allowed once but gets no rule.</summary>
    public const int CommandMax = 2_000;

    /// <summary>Live rules one user may hold.</summary>
    public const int LiveMax = 200;

    /// <summary>How long an ended rule (and the command it holds) is kept before it is deleted.</summary>
    public static readonly TimeSpan KeepEnded = TimeSpan.FromDays(7);

    /// <summary>The tool that runs a shell command; its rule holds the exact command.</summary>
    public const string ShellTool = "Bash";

}
