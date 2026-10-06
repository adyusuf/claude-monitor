using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// What this target lets remote runs do (ADR-0004, "Four fail-closed keys"): the exec level and an optional local
/// ceiling. Empty ceiling lists mean "no extra limit"; the level alone still applies. Unknown or unreadable means
/// <see cref="Off"/>.
/// </summary>
public sealed record ExecPolicy(string Level, IReadOnlyList<string> AllowedExecutables, IReadOnlyList<string> AllowedRoots)
{
    public static readonly ExecPolicy Off = new(ExecLevels.Off, [], []);

    public bool Allows(string mode) => ExecLevels.Allows(Level, mode);
}

/// <summary>The target's verdict on one run just before it would start. ResolvedExe is the real path of argv[0].</summary>
public sealed record ExecDecision(bool Allowed, string? Error, string? ResolvedExe)
{
    public static ExecDecision Refuse(string error) => new(false, error, null);
}
