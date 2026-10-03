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
