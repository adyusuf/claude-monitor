using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Data;

// Machines, agents and how they connect (docs/data-model.md §3).

public static class AgentStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public static class AgentTokenKinds
{
    public const string Access = "access";
    public const string Refresh = "refresh";
}

public static class DeviceStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Consumed = "consumed";
}

public sealed class Machine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public string MachineKeyHash { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string? OsVersion { get; set; }
    public string Arch { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class Agent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MachineId { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Version { get; set; } = "";
    public string Status { get; set; } = AgentStatuses.Active;
    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }

    /// <summary>What the agent last reported it lets remote runs do (ADR-0005); an agent that never reported is "off".</summary>
    public string ExecLevel { get; set; } = ClaudeMonitor.Contracts.ExecLevels.Off;
    public bool ServiceMode { get; set; }
    public DateTimeOffset? ProfileAt { get; set; }

    // ADR-0008 §3a: the role a workspace admin set and the claim the agent reports. NULL is "not set"; a claim only tightens.
    public string? RoleEnvironment { get; set; }
    public string? RoleFunction { get; set; }
    public DateTimeOffset? RoleSetAt { get; set; }
    public Guid? RoleSetBy { get; set; }
    public string? ClaimedEnvironment { get; set; }
    public string? ClaimedFunction { get; set; }

    // ADR-0008 §3e and §3a′: Claude Code on the machine and the tool inventory, as the agent reported them. NULL: an older agent (unknown).
    public string? ClaudeKind { get; set; }
    public string? ClaudePath { get; set; }
    public string? ClaudeVersion { get; set; }
    public int? ClaudeSessionsLive { get; set; }
    public int? ClaudeSessionsIdle { get; set; }
    public DateTimeOffset? PresenceAt { get; set; }
    /// <summary>JSON arrays of {name, path, version, trusted}; NULL = unknown, [] = none found.</summary>
    public string? Interpreters { get; set; }
    public string? Tools { get; set; }
    public string[]? Capabilities { get; set; }

    // ADR-0008 §3a′: what an admin typed (tags, note, untrusted text) and the opt-in listening-services report of the owner.
    /// <summary>JSON object of key=value pairs, at most ten; NULL = none.</summary>
    public string? Tags { get; set; }
    public DateTimeOffset? TagsSetAt { get; set; }
    public Guid? TagsSetBy { get; set; }
    public string? AdminNote { get; set; }
    public DateTimeOffset? AdminNoteSetAt { get; set; }
    public Guid? AdminNoteSetBy { get; set; }
    public bool ShareServices { get; set; }
    public DateTimeOffset? ShareServicesSetAt { get; set; }
    public Guid? ShareServicesSetBy { get; set; }
    /// <summary>JSON array of {port, proto, process}, at most 64; NULL = not reported (opt-in is off or an older agent).</summary>
    public string? Services { get; set; }
    public DateTimeOffset? ServicesAt { get; set; }

    /// <summary>What Claude Code has on the machine (ADR-0011 §7a, step E): one JSON document, replaced by every profile; NULL when the agent does not report it.</summary>
    public string? ClaudeInventory { get; set; }

    /// <summary>The machine owner's switch for reporting what Claude Code has on the machine (ADR-0011 §7a, step E); off until they say so. Off: the agent does not read it and the API keeps none.</summary>
    public bool ShareInventory { get; set; }
    public DateTimeOffset? ShareInventorySetAt { get; set; }
    public Guid? ShareInventorySetBy { get; set; }
    public DateTimeOffset? ClaudeInventoryAt { get; set; }

    /// <summary>ADR-0008 §3f: the machine's owner lets another machine of the same user ask a question of a Claude session here; off until they say so.</summary>
    public bool AcceptsAsks { get; set; }
    public DateTimeOffset? AcceptsAsksSetAt { get; set; }
    public Guid? AcceptsAsksSetBy { get; set; }

    /// <summary>ADR-0014: the machine's owner lets Claude be started and stopped on it from the web; off until they say so, and reset when the machine moves workspace.</summary>
    public bool AcceptsClaudeControl { get; set; }
    public DateTimeOffset? AcceptsClaudeControlSetAt { get; set; }
    public Guid? AcceptsClaudeControlSetBy { get; set; }

    /// <summary>ADR-0014 P3: what the daemon does by itself with Claude (<see cref="ClaudeAutoModes"/>); off until the owner says so, and off again whenever Claude control is off or the machine moves workspace.</summary>
    public string ClaudeAutoMode { get; set; } = ClaudeAutoModes.Off;

    /// <summary>The push transport the agent has on (<c>inbox</c> or <c>channel</c>), or null when it is off or the agent does not say.</summary>
    public string? PushTransport { get; set; }

    /// <summary>ADR-0010 §4: the machine only runs what a pinned owner key signed; the agent reports it with its profile. False for an older agent.</summary>
    public bool ApprovalsRequired { get; set; }
}

public sealed class AgentToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AgentId { get; set; }
    public string Kind { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ReplacedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class DeviceAuthorization
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string DeviceCodeHash { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string MachineKeyHash { get; set; } = "";
    public string RequestedHostname { get; set; } = "";
    public string RequestedOs { get; set; } = "";
    public string RequestedArch { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string Status { get; set; } = DeviceStatuses.Pending;
    public Guid? WorkspaceId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
}

/// <summary>
/// One project on one machine (ADR-0008 §3a′): the key a session reported or an admin listed, with what the agent read from the folder's
/// <c>.git</c> files. The folder path never leaves the machine; <see cref="PathHmac"/> is an HMAC under a per-agent key.
/// </summary>
public sealed class MachineProject
{
    public Guid AgentId { get; set; }
    public string ProjectKey { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? PathHmac { get; set; }
    public string? Branch { get; set; }
    public string? HeadSha { get; set; }
    public string? Tag { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string Source { get; set; } = ProjectSources.Session;

    /// <summary>The test command the person at the machine set for this folder and the folder path it runs in (ADR-0011 §7a); both NULL when there is none.</summary>
    public List<string>? TestArgv { get; set; }
    public string? TestCwd { get; set; }
}

/// <summary>
/// A question one machine of a user asks of another machine of the same user (ADR-0008 §3f). The question and the answer are untrusted text, personal
/// data like a command: masked before upload, never logged, exported, emptied on account deletion and deleted after the retention period.
/// </summary>
public sealed class MachineAsk
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid RequesterAgentId { get; set; }
    public Guid RequesterUserId { get; set; }
    public Guid? RequesterSessionId { get; set; }
    public Guid TargetAgentId { get; set; }
    public Guid TargetUserId { get; set; }
    /// <summary>The Claude session on the target that received the ask (its external id), set when it was delivered; the answer must come from it.</summary>
    public string? TargetSession { get; set; }
    public string ClientKey { get; set; } = "";
    public string? ProjectKey { get; set; }
    public string? Question { get; set; }
    public string Status { get; set; } = AskStatuses.Queued;
    public string? Answer { get; set; }
    public bool AnswerTruncated { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>When the target's daemon said it holds the ask (it is still <c>queued</c> until a session takes it).</summary>
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
