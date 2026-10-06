using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

/// <summary>Which versions an agent may move to. Only a strictly higher one: a replayed old manifest is a downgrade, not an update.</summary>
public static class UpdateRules
{
    public static bool TryParse(string? text, out Version version) => UpdateVersion.TryParse(text, out version);

    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var next) && TryParse(current, out var now) && next > now;

    /// <summary>The offer's minimum supported version is above the running one: this build is no longer supported and should update.</summary>
    public static bool IsBelowMinimum(string? minSupported, string? current) =>
        TryParse(minSupported, out var min) && TryParse(current, out var now) && now < min;
}
