namespace ClaudeMonitor.Contracts;

// The closed sets of remote work (ADR-0005). The database holds the same strings behind a CHECK, listed there in
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
        (Shell, RunModes.Argv or RunModes.Shell or RunModes.Script) => true,
        (Argv, RunModes.Argv) => true,
        _ => false,
    };
}

public static class RunModes
{
    public const string Argv = "argv";
    public const string Shell = "shell";

    /// <summary>A script text started by a trusted interpreter from a scratch file (ADR-0008 §3c); needs exec level shell, never matches a grant or a job.</summary>
    public const string Script = "script";
    public static readonly IReadOnlyList<string> All = [Argv, Shell, Script];
}

/// <summary>
/// The interpreters a script run may name (ADR-0008 §3c) and the inventory tools that can stand for them. The target must have reported one of
/// the tools as trusted. A script's size and the number of its arguments are limited here so the web, the API and the agent agree.
/// </summary>
public static class ScriptInterpreters
{
    public const string Sh = "sh";
    public const string Bash = "bash";
    public const string PowerShell = "powershell";
    public const string Python = "python";
    public static readonly IReadOnlyList<string> All = [Sh, Bash, PowerShell, Python];
    public const int BodyMax = 32_768;
    public const int BodyCeiling = 65_536;
    public const int ArgsMax = 32;
    public const int ArgChars = 1024;

    /// <summary>The inventory tool names (<see cref="ToolNames"/>) any of which can run a script of this interpreter; empty for an unknown one.</summary>
    public static IReadOnlyList<string> Tools(string? interpreter) => interpreter switch
    {
        Sh => [ToolNames.Sh],
        Bash => [ToolNames.Bash],
        Python => [ToolNames.Python],
        PowerShell => [ToolNames.PowerShell, ToolNames.WindowsPowerShell],
        _ => [],
    };

    /// <summary>The file extension of the scratch file; null for an unknown interpreter.</summary>
    public static string? Extension(string? interpreter) => interpreter switch
    {
        Sh or Bash => ".sh",
        Python => ".py",
        PowerShell => ".ps1",
        _ => null,
    };
}

/// <summary>What a run was for, so a machine's project history can group results (ADR-0008 §3a′). Null means the run named none.</summary>
public static class RunKinds
{
    public const string Test = "test";
    public const string Build = "build";
    public const string Deploy = "deploy";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Test, Build, Deploy, Other];
    public const int ProjectKeyMax = 200;
}

/// <summary>Where the agent learned about a project folder: a session that reported it, or an admin-listed folder (ADR-0008 §3a′).</summary>
public static class ProjectSources
{
    public const string Session = "session";
    public const string Listed = "listed";
    public static readonly IReadOnlyList<string> All = [Session, Listed];
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

    /// <summary>A role value outside the closed sets (ADR-0008 §3a).</summary>
    public const string InvalidMachineRole = "invalid_machine_role";

    /// <summary>A tag set that breaks the limits of <c>MachineTags</c> (ADR-0008 §3a′).</summary>
    public const string InvalidTag = "invalid_tag";

    /// <summary>A script run against a machine whose environment no admin has set (ADR-0008 §3c).</summary>
    public const string RoleNotSet = "role_not_set";

    /// <summary>The target has not reported a trusted tool for the script's interpreter.</summary>
    public const string InterpreterMissing = "interpreter_missing";

    /// <summary>A script for a strict target needs the approver's two-step code and they have no two-step sign-in.</summary>
    public const string TotpRequired = "totp_required";

    /// <summary>The target became strict after the run was approved without a code; the run was cancelled instead of delivered.</summary>
    public const string RoleChanged = "role_changed";

    public const string InvalidScript = "invalid_script";
    public const string InvalidFiles = "invalid_files";
    public const string InvalidSchedule = "invalid_schedule";
    public const string SchedulesDisabled = "schedules_disabled";
    public const string ScheduleStrict = "schedule_strict";
    public const string SchedulePlaceholder = "schedule_placeholder";
    public const string ScheduleLimit = "schedule_limit";
    public const string ScheduleApprovals = "schedule_approvals";
    public const string ApprovalKeyLimit = "approval_key_limit";
    public const string InvalidApprovalKey = "invalid_approval_key";
    public const string FilesUnsupported = "files_unsupported";
    public const string InvalidInterpreter = "invalid_interpreter";

    /// <summary>An admin note with control characters or over the limit (ADR-0008 §3a′).</summary>
    public const string InvalidNote = "invalid_note";
}
