using Podium.Contracts;

namespace Podium.Agent.Exec;

/// <summary>
/// What this target lets remote runs do (ADR-0005, "Four fail-closed keys"): the exec level and an optional local
/// ceiling. Empty ceiling lists mean "no extra limit"; the level alone still applies. Unknown or unreadable means
/// <see cref="Off"/>. The ceiling of ADR-0020 §4 adds folders a run may never use (DeniedRoots) and the SHA-256 an allowed
/// executable's file must have (ExecutableHashes, by the executable's path as listed).
/// </summary>
public sealed record ExecPolicy(string Level, IReadOnlyList<string> AllowedExecutables, IReadOnlyList<string> AllowedRoots,
    string? ClaimedEnvironment = null, string? ClaimedFunction = null, IReadOnlyList<string>? ProjectFolders = null, string? RunAccount = null,
    IReadOnlyList<string>? DeniedRoots = null, IReadOnlyDictionary<string, string>? ExecutableHashes = null)
{
    public static readonly ExecPolicy Off = new(ExecLevels.Off, [], []);

    /// <summary>A ceiling (allowed executables, allowed or denied roots) limits what runs; free shell text and a script could not be held to it, so a policy with one never allows either.</summary>
    public bool HasCeiling => AllowedExecutables.Count > 0 || AllowedRoots.Count > 0 || DeniedRoots is { Count: > 0 };

    /// <summary>The ceiling allows a shell or script run: the level does and no list limits it.</summary>
    public bool CeilingAllowsShell => ExecLevels.Allows(Level, RunModes.Shell) && !HasCeiling;

    public bool Allows(string mode) => ExecLevels.Allows(Level, mode) && !(HasCeiling && mode is RunModes.Shell or RunModes.Script);
}

/// <summary>
/// The target's verdict on one run just before it would start. ResolvedExe is the real path of argv[0]; ResolvedCwd the real working directory the run
/// starts in (null: the work folder) and ResolvedPaths the real paths a grant's placeholders filled. A refusal by an access layer (ADR-0020 §4) names the
/// layer (RefusedBy, <see cref="AccessRefusedBy"/>) and the policy version.
/// </summary>
public sealed record ExecDecision(bool Allowed, string? Error, string? ResolvedExe, string? ResolvedCwd = null, IReadOnlyList<string>? ResolvedPaths = null,
    string? RefusedBy = null, int? AccessVersion = null)
{
    public static ExecDecision Refuse(string error) => new(false, error, null);

    public static ExecDecision Refuse(AccessRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new(false, refusal.Code, null, RefusedBy: refusal.RefusedBy, AccessVersion: refusal.Version);
    }
}
