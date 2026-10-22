using System.Text;
using System.Text.Json;
using Podium.Agent.Config;

namespace Podium.Agent.Daemon;

/// <summary>
/// What Claude Code's own trust record says about a folder (ADR-0014 P2): the flag <c>hasTrustDialogAccepted</c> that Claude Code keeps per project folder in its
/// <c>.claude.json</c>. Read only, never written, and never parsed beyond that one flag. Trust is per exact folder: a trusted parent does not cover its children
/// (measured on 2.1.294). Claude Code stays the one that decides; this only tells the page and the agent early that a start would be refused. An unreadable or
/// unknown record answers null, and then nothing is refused in advance.
/// </summary>
public static class ClaudeTrust
{
    /// <summary>true: the folder is trusted; false: Claude Code has not been told to trust it; null: the record could not be read.</summary>
    public static bool? IsTrusted(AgentConfig config, string folder)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(folder) || !Path.IsPathRooted(folder)) return null;
        try
        {
            var file = RecordPath(config);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var wanted = Forms(folder);
            if (!doc.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object) return false;
            foreach (var entry in projects.EnumerateObject())
            {
                if (!Forms(entry.Name).Overlaps(wanted)) continue;
                if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("hasTrustDialogAccepted", out var flag) && flag.ValueKind == JsonValueKind.True) return true;
            }

            return false;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Claude Code's record: <c>~/.claude.json</c> normally, <c>$CLAUDE_CONFIG_DIR/.claude.json</c> when the configuration folder is not the default one.</summary>
    public static string RecordPath(AgentConfig config)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isDefault = string.Equals(Path.TrimEndingDirectorySeparator(config.ClaudeConfigDir), Path.Combine(profile, ".claude"), StringComparison.Ordinal);
        return isDefault ? Path.Combine(profile, ".claude.json") : Path.Combine(config.ClaudeConfigDir, ".claude.json");
    }

    /// <summary>The ways one folder can be written: as given and with its symbolic links followed (macOS keeps /tmp and /var under /private), compared without case where the system ignores it.</summary>
    private static HashSet<string> Forms(string folder)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return new HashSet<string>([full, Real(full)], comparer);
    }

    private static string Real(string full)
    {
        var current = Path.GetPathRoot(full) ?? "";
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) current = Path.TrimEndingDirectorySeparator(target.FullName);
        }

        return current;
    }
}
