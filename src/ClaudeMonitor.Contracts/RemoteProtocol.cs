namespace ClaudeMonitor.Contracts;

// The agent protocol of remote work (ADR-0005). Like AgentProtocol.cs it only GROWS (global #4): fields are added at
// the end of a record with a default, never removed, renamed or retyped.

/// <summary>What a target tells the API about itself after every start and every settings change.</summary>
public sealed record AgentProfile(string ExecLevel, bool ServiceMode, string? OsVersion, string? OsAccount, int MaxConcurrent,
    string? ClaimedEnvironment = null, string? ClaimedFunction = null, ClaudePresence? Claude = null,
    IReadOnlyList<ToolInfo>? Interpreters = null, IReadOnlyList<ToolInfo>? Tools = null, IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// Claude Code on the agent's machine (ADR-0008 §3e): how it is installed (<see cref="ClaudeKinds"/>), where (home-relative, never
/// shown to other members), its version, and how many sessions run and how many of them are idle. An older agent sends none.
/// </summary>
public sealed record ClaudePresence(string Kind, string? Path, string? Version, int SessionsLive, int SessionsIdle, DateTimeOffset SeenAt);

/// <summary>
/// One tool of the inventory (ADR-0008 §3a′): its name (<see cref="ToolNames"/>), its resolved path (home-relative), the first line of
/// its --version (null when the agent did not run it: only a trusted or self-owned binary is run), and whether it passes the
/// ADR-0005 executable trust check. Only a trusted interpreter may run a script.
/// </summary>
public sealed record ToolInfo(string Name, string Path, string? Version, bool Trusted);

public sealed record DiskSample(string Mount, long UsedBytes, long TotalBytes);

public sealed record MetricSample(DateTimeOffset SampledAt, double CpuPct, long MemUsedBytes, long MemTotalBytes,
    IReadOnlyList<DiskSample> Disks);

public sealed record MetricsReport(IReadOnlyList<MetricSample> Samples);

/// <summary>An alert the agent opened or resolved. Subject is the disk mount, or empty for cpu and memory.</summary>
public sealed record AlertReport(string Kind, string Subject, string State, double Value, double ThresholdPct, DateTimeOffset At);

/// <summary>The workspace's alert thresholds, in percent; a breach counts once it lasts SustainSeconds.</summary>
public sealed record AlertThresholds(int CpuPct, int MemoryPct, int DiskPct, int SustainSeconds);

/// <summary>An agent of the workspace as a requester sees it, for choosing a target.</summary>
public sealed record MachineView(Guid AgentId, string Hostname, string Os, string UserName, string ExecLevel, bool ServiceMode,
    bool Online, DateTimeOffset? LastSeenAt, MetricSample? Latest, int OpenAlerts,
    string? RoleEnvironment = null, string? RoleFunction = null, string? ClaimedEnvironment = null, string? ClaimedFunction = null,
    string? EffectiveEnvironment = null, string? EffectiveFunction = null, string? ClaudeKind = null, string? ClaudeVersion = null,
    int? ClaudeSessionsIdle = null, IReadOnlyList<ToolInfo>? Interpreters = null, IReadOnlyList<ToolInfo>? Tools = null,
    IReadOnlyList<string>? Capabilities = null, DateTimeOffset? PresenceAt = null);

public sealed record AlertView(Guid Id, Guid AgentId, string Hostname, string Kind, string Subject, string State,
    double ThresholdPct, double LastValue, double PeakValue, DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// A requester asks for a run. ClientKey is the requester's own id for the request: sent again, it returns the same run.
/// Exactly one of Argv (mode argv) and ShellCommand (mode shell) is set.
/// </summary>
public sealed record RunCreate(string ClientKey, Guid TargetAgentId, string Mode, IReadOnlyList<string>? Argv, string? ShellCommand,
    string? Cwd, int TimeoutSeconds, string? Reason, string? SessionExternalId = null, Guid? JobId = null);

public sealed record RunCreated(Guid Id, string Status, Guid? GrantId, DateTimeOffset ExpiresAt);

public sealed record RunView(Guid Id, Guid TargetAgentId, string TargetHostname, string Mode, IReadOnlyList<string>? Argv,
    string? ShellCommand, string? Cwd, int TimeoutSeconds, string Status, int? ExitCode, string? Error, long OutputBytes,
    bool OutputTruncated, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt, Guid? GrantId, Guid? JobId);

/// <summary>One piece of a run's output, numbered from 0 by the target. GapBefore marks output cut out before it.</summary>
public sealed record RunOutputChunk(int Seq, string Stream, string Body, bool GapBefore = false);

public sealed record RunOutputPage(IReadOnlyList<RunOutputChunk> Chunks, int NextSeq, bool Done);

/// <summary>A grant as the target re-checks it: a fixed-length argv template, its literal working directory and longest timeout.</summary>
public sealed record GrantTemplate(IReadOnlyList<string> Argv, string Cwd, int MaxTimeoutSeconds);

/// <summary>
/// An approved run sent to its target. NotAfter: the target never starts it later than this. DecidedAt: when the server approved it
/// (server clock; null from an older API), so a target that refuses a run for an off switch knows whether its last settings read is older.
/// </summary>
public sealed record RunMessage(Guid Id, string Mode, IReadOnlyList<string>? Argv, string? ShellCommand, string? Cwd,
    int TimeoutSeconds, DateTimeOffset NotAfter, Guid? GrantId, GrantTemplate? Grant, DateTimeOffset? DecidedAt = null);

public sealed record RunCancelMessage(Guid Id);

/// <summary>Sent to the requester's agent when one of its runs changes, so it fetches the run without waiting for its poll.</summary>
public sealed record RunUpdateMessage(Guid Id, string Status);

/// <summary>The target's report on a run. At is when it happened on the target.</summary>
public sealed record RunStatusUpdate(string Status, int? ExitCode = null, string? Error = null, bool OutputTruncated = false,
    string? ResolvedExe = null, DateTimeOffset? At = null);

public sealed record GrantRequest(Guid TargetAgentId, IReadOnlyList<string> Template, string Cwd, int MaxTimeoutSeconds, int Days,
    string? Reason);

public sealed record GrantView(Guid Id, Guid TargetAgentId, IReadOnlyList<string> Template, string Cwd, int MaxTimeoutSeconds,
    string Status, DateTimeOffset ExpiresAt, int UseCount, DateTimeOffset? LastUsedAt);

public sealed record JobProposal(Guid TargetAgentId, string Name, IReadOnlyList<string> Argv, string Cwd, int TimeoutSeconds,
    string? Reason);

public sealed record JobView(Guid Id, Guid TargetAgentId, string Name, IReadOnlyList<string> Argv, string Cwd, int TimeoutSeconds,
    string Status, DateTimeOffset CreatedAt);
