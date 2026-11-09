namespace Podium.Contracts;

// The access policy of a managed project (ADR-0020 §4): what the web sets, what the API delivers to agents and what an agent reports back.
// Like the other protocol files it only GROWS (global #4): fields are added at the end of a record with a default, never removed, renamed or retyped.

/// <summary>
/// A project's access policy as the owners and admins set it and sign it (ADR-0020 §4). It covers the repositories it names (repo keys,
/// <see cref="RepoKeys"/>) and only narrows what a machine's own ceiling allows. Cleared is a tombstone: the policy was taken back and the default
/// applies again. Canonical form and limits: <see cref="AccessPolicyRules"/>; hash and signature: <see cref="AccessPolicySigning"/>.
/// </summary>
public sealed record AccessPolicyDocument(int Format, IReadOnlyList<string> ProjectKeys, bool IncludeProjectFolder, AccessOsRules Linux,
    AccessOsRules MacOs, AccessOsRules Windows, bool Shell, bool Cleared);

/// <summary>One OS's section of a policy: absolute folders a run may use, folders it may never use, and the executables it may start (empty: no limit).</summary>
public sealed record AccessOsRules(IReadOnlyList<string> AllowedFolders, IReadOnlyList<string> DeniedFolders, IReadOnlyList<AccessApp> Applications);

/// <summary>An allowed executable by its absolute path, with the lowercase hex SHA-256 its file must have when one is given.</summary>
public sealed record AccessApp(string Path, string? Sha256);

/// <summary>A policy as the API delivers it to an agent: the document, its version and hash, and the owner's signature when there is one.</summary>
public sealed record AccessPolicyMessage(Guid ProjectId, Guid WorkspaceId, int Version, AccessPolicyDocument Document, string Hash, string? KeyId,
    string? Signature);

/// <summary>The policies of the repositories an agent reports, tombstones included. Stamp is what <see cref="AgentSettings.AccessPolicyStamp"/> carries.</summary>
public sealed record AgentAccessPolicies(string Stamp, IReadOnlyList<AccessPolicyMessage> Policies);

/// <summary>
/// The web saves a project's policy. ExpectedVersion is the version it edited (0 for none yet): another save in between is refused. KeyId and
/// Signature sign version ExpectedVersion + 1 when this browser holds an approval key; Code is the re-authentication code.
/// </summary>
public sealed record AccessPolicySave(int ExpectedVersion, AccessPolicyDocument Document, string? KeyId, string? Signature, string? Code);

/// <summary>
/// A project's policy as the project page shows it. IsDefault: no policy was saved yet (version 0, the default document). CandidateKeys: the repo keys of
/// the project's applications the policy may cover. MachinesRequiringSignature: machines that require signed approvals and will ignore an unsigned version.
/// </summary>
public sealed record AccessPolicyView(Guid ProjectId, int Version, AccessPolicyDocument Document, string Hash, bool IsDefault, bool IsSigned,
    string? SignerName, IReadOnlyList<string> CandidateKeys, int MachinesRequiringSignature, bool CanEdit);

/// <summary>
/// The machine's own ceiling, set by its admin (<c>aipod config access</c>): folders and executables a run may use (empty: no limit) and whether a shell
/// or script run is allowed at all. Shown only to the machine's owner and the workspace's admins.
/// </summary>
public sealed record AccessCeilingReport(IReadOnlyList<string> AllowedFolders, IReadOnlyList<string> DeniedFolders, IReadOnlyList<AccessApp> Applications,
    bool Shell);

/// <summary>What happened to one delivered policy on the machine: its version, one of <see cref="AccessPolicyStates"/>, whether it is signed (IsSigned; the name avoids the type-name rule CA1720) and by which key.</summary>
/// <param name="ProjectFolders">How many folders on this machine belong to the policy's repositories (never their paths).</param>
public sealed record AccessAppliedReport(Guid ProjectId, int Version, string State, bool IsSigned, string? KeyId, int ProjectFolders = 0);

/// <summary>An agent's access report, part of its profile: the ceiling and the state of each policy it received.</summary>
public sealed record AccessReport(AccessCeilingReport Ceiling, IReadOnlyList<AccessAppliedReport> Policies);

/// <summary>
/// One project's policy on one machine as the machine page shows it: the web's version, what the machine applied and in which state, and the effective
/// lists for the machine's OS (the web policy narrowed by the ceiling).
/// </summary>
public sealed record MachineAccessPolicyView(Guid ProjectId, string ProjectName, int WebVersion, int? AppliedVersion, string? State, bool IsSigned,
    IReadOnlyList<string> AllowedFolders, IReadOnlyList<string> DeniedFolders, IReadOnlyList<AccessApp> Applications, bool IncludeProjectFolder, bool Shell);

/// <summary>
/// A machine's access as the machine page shows it. Ceiling is null for members who are not the owner or an admin, and when the agent sent no report
/// (ReportedAt null: an agent too old to apply policies). DefaultApplies: a run with no policy gets the default here (the switch is on or the machine is strict).
/// </summary>
public sealed record MachineAccessView(AccessCeilingReport? Ceiling, IReadOnlyList<MachineAccessPolicyView> Policies, DateTimeOffset? ReportedAt,
    bool DefaultApplies);

/// <summary>The state of a delivered policy on a machine. Anything but <see cref="Applied"/> and <see cref="KeptSigned"/> means the default applies.</summary>
public static class AccessPolicyStates
{
    public const string Applied = "applied";

    /// <summary>A newer version was not signed, so the last signed one stays.</summary>
    public const string KeptSigned = "kept_signed";
    public const string Default = "default";
    public const string UnsignedIgnored = "unsigned_ignored";
    public const string KeyUnknown = "key_unknown";
    public const string Invalid = "invalid";
    public const string RollbackRefused = "rollback_refused";
    public static readonly IReadOnlyList<string> All = [Applied, KeptSigned, Default, UnsignedIgnored, KeyUnknown, Invalid, RollbackRefused];
}

/// <summary>Why a target refused a run under the access layers (ADR-0020 §4), as stable codes in <see cref="RunStatusUpdate.Error"/>.</summary>
public static class AccessErrors
{
    public const string Prefix = "access_";
    public const string CwdNotAllowed = "access_cwd_not_allowed";
    public const string CwdDenied = "access_cwd_denied";
    public const string PathNotAllowed = "access_path_not_allowed";
    public const string PathDenied = "access_path_denied";
    public const string ExeNotAllowed = "access_exe_not_allowed";
    public const string ExeHash = "access_exe_hash";
    public const string ShellRefused = "access_shell_refused";
    public const string ScriptRefused = "access_script_refused";

    /// <summary>A path or the executable could not be resolved to a real path, so it cannot be judged.</summary>
    public const string Unresolved = "access_unresolved";

    /// <summary>The run names a policy stamp the agent could not catch up with.</summary>
    public const string Stale = "access_stale";

    public static readonly IReadOnlyList<string> All =
        [CwdNotAllowed, CwdDenied, PathNotAllowed, PathDenied, ExeNotAllowed, ExeHash, ShellRefused, ScriptRefused, Unresolved, Stale];
}

/// <summary>Which layer refused a run, in <see cref="RunStatusUpdate.RefusedBy"/>.</summary>
public static class AccessRefusedBy
{
    public const string Ceiling = "ceiling";
    public const string Policy = "policy";
    public const string Default = "default";
    public static readonly IReadOnlyList<string> All = [Ceiling, Policy, Default];
}

/// <summary>Where a path tool's target lies against the session's project layers (ADR-0019 tie-in), in <see cref="PermissionRequestCreate.AccessScope"/>.</summary>
public static class AccessScopes
{
    public const string Inside = "inside";
    public const string Outside = "outside";
    public const string Unknown = "unknown";
    public static readonly IReadOnlyList<string> All = [Inside, Outside, Unknown];
}

/// <summary>Why the API refused a policy (<see cref="AccessPolicyRules.Validate"/> and the save endpoint).</summary>
public static class AccessPolicyErrors
{
    public const string Malformed = "policy_malformed";
    public const string Format = "policy_format";
    public const string Keys = "policy_keys";
    public const string TooMany = "policy_too_many";
    public const string Path = "policy_path";
    public const string ForbiddenRoot = "policy_forbidden_root";
    public const string App = "policy_app";
    public const string Sha256 = "policy_sha256";
    public const string Cleared = "policy_cleared";
    public const string TooLarge = "policy_too_large";

    /// <summary>The body is valid but not in canonical form; the API never rewrites it (a signature covers it as sent).</summary>
    public const string NotCanonical = "policy_not_canonical";

    /// <summary>Another save came first (the version moved on).</summary>
    public const string Changed = "access_policy_changed";

    /// <summary>A repo key already belongs to another project's policy.</summary>
    public const string KeyTaken = "access_key_taken";
}
