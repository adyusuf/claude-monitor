namespace ClaudeMonitor.Contracts;

// The closed sets of remote work (ADR-0004). The database holds the same strings behind a CHECK, listed there in
// this order; code that switches on them always has a default branch.

/// <summary>What a target lets remote runs do, set on the target machine only. Unknown means off (fail-closed).</summary>
public static class ExecLevels
{
    public const string Off = "off";
    public const string Argv = "argv";
    public const string Shell = "shell";
    public static readonly IReadOnlyList<string> All = [Off, Argv, Shell];

    /// <summary>True when a run of this mode may execute at this level.</summary>
    public static bool Allows(string? level, string mode) => (level, mode) switch
    {
        (Shell, RunModes.Argv or RunModes.Shell) => true,
        (Argv, RunModes.Argv) => true,
        _ => false,
    };
}

public static class RunModes
{
    public const string Argv = "argv";
    public const string Shell = "shell";
    public static readonly IReadOnlyList<string> All = [Argv, Shell];
}

public static class RunStatuses
{
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Delivered = "delivered";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All =
        [PendingApproval, Approved, Delivered, Running, Succeeded, Failed, TimedOut, Denied, Expired, Cancelled];

    /// <summary>A run in one of these will not change again.</summary>
    public static readonly IReadOnlySet<string> Final = new HashSet<string> { Succeeded, Failed, TimedOut, Denied, Expired, Cancelled };

    /// <summary>Approved and not yet finished: the target owes an answer.</summary>
    public static readonly IReadOnlySet<string> Live = new HashSet<string> { Approved, Delivered, Running };

    /// <summary>What a target may report back.</summary>
    public static readonly IReadOnlySet<string> FromAgent = new HashSet<string> { Delivered, Running, Succeeded, Failed, TimedOut, Cancelled };
}

public static class RunStreams
{
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";
    public static readonly IReadOnlyList<string> All = [Stdout, Stderr];
}

public static class AlertKinds
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Disk = "disk";

    /// <summary>Raised by the API, never by an agent: a service agent stopped reporting.</summary>
    public const string Offline = "offline";
    public static readonly IReadOnlyList<string> All = [Cpu, Memory, Disk, Offline];
    public static readonly IReadOnlySet<string> FromAgent = new HashSet<string> { Cpu, Memory, Disk };
}

public static class AlertStates
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public static readonly IReadOnlyList<string> All = [Open, Resolved];
}

public static class GrantStatuses
{
    public const string Requested = "requested";
    public const string Active = "active";
    public const string Denied = "denied";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public static readonly IReadOnlyList<string> All = [Requested, Active, Denied, Revoked, Expired];
}

public static class JobStatuses
{
    public const string Proposed = "proposed";
    public const string Active = "active";
    public const string Denied = "denied";
    public const string Retired = "retired";
    public static readonly IReadOnlyList<string> All = [Proposed, Active, Denied, Retired];
}

/// <summary>Why a remote request was refused, as the API answers it in a 409 body and the MCP tools show it.</summary>
public static class RemoteErrors
{
    public const string Disabled = "remote_runs_disabled";
    public const string TargetCannotRun = "target_cannot_run";
    public const string TargetBusy = "target_busy";
    public const string MfaRequired = "mfa_required";
    public const string Mismatch = "run_mismatch";
    public const string NotPending = "not_pending";
    public const string BadTemplate = "bad_template";
    public const string Ambiguous = "ambiguous_machine";
}
