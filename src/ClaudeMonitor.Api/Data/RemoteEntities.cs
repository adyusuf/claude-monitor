using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Data;

// Remote work: metrics, alerts, runs, grants and jobs (docs/data-model.md §3b, ADR-0004). Status strings are the
// closed sets of Contracts/RemoteCodes.cs.

public sealed class MachineMetric
{
    public Guid AgentId { get; set; }
    public DateTimeOffset SampledAt { get; set; }
    public Guid WorkspaceId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public float CpuPct { get; set; }
    public long MemUsedBytes { get; set; }
    public long MemTotalBytes { get; set; }
    public string Disks { get; set; } = "[]";
}

public sealed class MachineAlert
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid AgentId { get; set; }
    public string Kind { get; set; } = "";
    public string Subject { get; set; } = "";
    public string State { get; set; } = AlertStates.Open;
    public float ThresholdPct { get; set; }
    public float LastValue { get; set; }
    public float PeakValue { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class RemoteRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid RequesterAgentId { get; set; }
    public Guid RequesterUserId { get; set; }
    public Guid? RequesterSessionId { get; set; }
    public Guid TargetAgentId { get; set; }
    public Guid TargetUserId { get; set; }
    public string ClientKey { get; set; } = "";
    public string Mode { get; set; } = RunModes.Argv;
    public List<string>? Argv { get; set; }
    public string? ShellCommand { get; set; }
    public string? Cwd { get; set; }
    public string? ResolvedExe { get; set; }
    public int TimeoutSeconds { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = RunStatuses.PendingApproval;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public long OutputBytes { get; set; }
    public bool OutputTruncated { get; set; }
    public Guid? GrantId { get; set; }
    public Guid? JobId { get; set; }
}

public sealed class RemoteRunOutput
{
    public Guid RunId { get; set; }
    public int Seq { get; set; }
    public string Stream { get; set; } = RunStreams.Stdout;
    public string Body { get; set; } = "";
    public int Bytes { get; set; }
    public bool GapBefore { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class MachineGrant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid TargetAgentId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid GranteeUserId { get; set; }
    public Guid? GranteeAgentId { get; set; }
    public Guid? RequestedByAgentId { get; set; }
    public List<string>? Template { get; set; }
    public string? Cwd { get; set; }
    public int MaxTimeoutSeconds { get; set; }
    public string TemplateHash { get; set; } = "";
    public string? Reason { get; set; }
    public string Status { get; set; } = GrantStatuses.Requested;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class MachineJob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid TargetAgentId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string NameSearch { get; set; } = "";
    public List<string>? Argv { get; set; }
    public string? Cwd { get; set; }
    public int TimeoutSeconds { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = JobStatuses.Proposed;
    public Guid ProposedByUserId { get; set; }
    public Guid? ProposedByAgentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}
