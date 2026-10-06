using System.Globalization;

namespace ClaudeMonitor.Agent.Update;

/// <summary>Which versions an agent may move to. Only a strictly higher one: a replayed old manifest is a downgrade, not an update.</summary>
public static class UpdateRules
{
    /// <summary>A plain x.y.z (numbers only, no sign, no pre-release tail): anything else is not a version we accept.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var parts = (text ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Length > 9 || !p.All(char.IsAsciiDigit))) return false;
        version = new Version(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
        return true;
    }

    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var next) && TryParse(current, out var now) && next > now;

    /// <summary>The offer's minimum supported version is above the running one: this build is no longer supported and should update.</summary>
    public static bool IsBelowMinimum(string? minSupported, string? current) =>
        TryParse(minSupported, out var min) && TryParse(current, out var now) && now < min;
}
