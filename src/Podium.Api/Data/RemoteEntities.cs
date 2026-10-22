using Podium.Contracts;

namespace Podium.Api.Data;

// Remote work: metrics, alerts, runs, grants and jobs (docs/data-model.md §3b, ADR-0005). Status strings are the
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

    /// <summary>The project this run was for (ADR-0008 §3a′), with its <see cref="Kind"/>; both NULL when the run named none.</summary>
    public string? ProjectKey { get; set; }
    public string? Kind { get; set; }

    /// <summary>The counts parsed from the output of a <c>test</c> run of a known tool (ADR-0008 §9.16); NULL when none was recognised.</summary>
    public int? TestsPassed { get; set; }
    public int? TestsFailed { get; set; }
    public int? TestsSkipped { get; set; }

    // ADR-0008 §3c, §3d: a script run's text, the role the approval card showed, the output cap frozen at creation and what was read, dropped and refused.
    public string? ScriptInterpreter { get; set; }
    public string? ScriptBody { get; set; }
    public List<string>? ScriptArgs { get; set; }

    /// <summary>The files a script run carries as JSON (jsonb), and the text written to its stdin (ADR-0010 §2); both null for a run without.</summary>
    public string? ScriptFilesJson { get; set; }
    public string? ScriptStdin { get; set; }

    /// <summary>The owner's signature of this run (ADR-0010 §4): the key's fingerprint, the signature (base64) and the expiry that was signed; all null for an approval without a key.</summary>
    public string? ApprovalKeyId { get; set; }
    public string? ApprovalSignature { get; set; }
    public DateTimeOffset? ApprovalNotAfter { get; set; }

    /// <summary>The schedule whose tick created this run (ADR-0010 §3); null for a run somebody asked for.</summary>
    public Guid? ScheduleId { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<Podium.Contracts.ScriptFile>? ScriptFiles
    {
        get => ScriptFilesJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<Podium.Contracts.ScriptFile>>(ScriptFilesJson, FilesJson);
        set => ScriptFilesJson = value is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(value, FilesJson) : null;
    }

    private static readonly System.Text.Json.JsonSerializerOptions FilesJson = new(System.Text.Json.JsonSerializerDefaults.Web);
    public string? TargetEnvironment { get; set; }
    public string? TargetFunction { get; set; }
    public bool ApprovedWithTotp { get; set; }
    public long? OutputCap { get; set; }
    public long? OutputReadBytes { get; set; }
    public long? OutputDroppedBytes { get; set; }
    public long? OutputRefusedBytes { get; set; }
    public int? OutputMissingFromSeq { get; set; }
    public int? OutputChunks { get; set; }
    public bool? OutputComplete { get; set; }
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

/// <summary>An approved job run on a schedule by the API (ADR-0010 §3): the owner of the job's target created it, and it fires ordinary runs the job approves.</summary>
public sealed class JobSchedule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid JobId { get; set; }
    public Guid TargetAgentId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Kind { get; set; } = ScheduleKinds.Interval;
    public int? IntervalMinutes { get; set; }
    public int? DailyAtMinutes { get; set; }
    public string Status { get; set; } = ScheduleStatuses.Active;
    public string? PauseReason { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>The run of the last tick, and whether its outcome was counted yet (a schedule fires no new run while the last is open).</summary>
    public Guid? LastRunId { get; set; }
    public bool LastRunSettled { get; set; } = true;
    public int FailuresInARow { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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

/// <summary>The public half of an owner's approval key (ADR-0010 §4): the browser keeps the private half and never sends it.</summary>
public sealed class ApprovalKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}
