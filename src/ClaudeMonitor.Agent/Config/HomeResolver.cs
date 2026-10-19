using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// Which folder is the agent's home when no variable says (ADR-0013, step 1). A machine keeps the folder it already uses; a new one gets the
/// aipod folder; nothing is moved or copied. "Already uses" means the folder holds the agent's own files (the identity file or the database): an
/// empty folder someone made, or a folder a tool created by accident, never takes a working agent's data out of sight. The aipod folder wins when
/// both hold files, because only a process that resolved to it could have written them there.
/// </summary>
public static class HomeResolver
{
    /// <summary>The file names that show a folder is an agent home (the same two <see cref="AgentConfig.IdentityPath"/> and <see cref="AgentConfig.DatabasePath"/> use).</summary>
    public static readonly IReadOnlyList<string> Markers = ["agent.json", "agent.db"];

    /// <summary>macOS: ~/Library/Application Support/aipod; Windows and Linux: ~/.aipod.</summary>
    public static string NewHomeFor(string os, string userProfile) =>
        os == OsKinds.MacOs ? Path.Combine(userProfile, "Library", "Application Support", "aipod") : Path.Combine(userProfile, ".aipod");

    /// <summary>The home of the agent versions before the rename (see <see cref="AgentConfig.HomeFor(string, string)"/>), still used by every install that has it.</summary>
    public static string OldHomeFor(string os, string userProfile) => AgentConfig.HomeFor(os, userProfile);

    /// <summary>The aipod folder when it holds an agent's files, else the old one when it does, else the aipod folder (a first install).</summary>
    public static string Resolve(string os, string userProfile, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var fresh = NewHomeFor(os, userProfile);
        if (Holds(fresh, fileExists)) return fresh;
        var old = OldHomeFor(os, userProfile);
        return Holds(old, fileExists) ? old : fresh;
    }

    private static bool Holds(string folder, Func<string, bool> fileExists) => Markers.Any(m => fileExists(Path.Combine(folder, m)));
}
