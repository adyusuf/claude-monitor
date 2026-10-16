using ClaudeMonitor.Agent.Auth;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// The project folders an admin or the machine's user listed for this machine (ADR-0008 §3a′), for machines without Claude Code or for
/// folders no session visits. A service agent reads them from the admin-owned policy file; an interactive or standalone agent keeps them in
/// agent.json (<c>cm-agent config projects add|remove|list</c>). Only absolute paths of existing folders are kept, at most <see cref="Max"/>.
/// </summary>
public static class ProjectFolderList
{
    public const int Max = 50;

    public static bool IsAbsolute(string? path) =>
        path is { Length: > 0 and <= 1024 } && !path.Any(char.IsControl) && (path.StartsWith('/') || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'));

    /// <summary>The folders saved in agent.json, empty when there are none.</summary>
    public static IReadOnlyList<string> Saved(AgentConfig config) => Identity.Peek(config)?.ProjectFolders ?? [];

    /// <summary>Adds a folder (it must be an absolute path of an existing folder); returns false when it is invalid, already listed, or the list is full.</summary>
    public static bool Add(AgentConfig config, string path, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsAbsolute(path) || !(exists ?? Directory.Exists)(path)) return false;
        var identity = Identity.Load(config);
        var folders = identity.ProjectFolders ?? [];
        var full = path.Length > 1 ? path.TrimEnd('/', '\\') : path;
        if (folders.Contains(full, StringComparer.Ordinal) || folders.Count >= Max) return false;
        (identity with { ProjectFolders = [.. folders, full] }).Save(config);
        return true;
    }

    /// <summary>Removes a listed folder; returns false when it was not listed.</summary>
    public static bool Remove(AgentConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        var identity = Identity.Load(config);
        var folders = identity.ProjectFolders ?? [];
        var full = path.Length > 1 ? path.TrimEnd('/', '\\') : path;
        if (!folders.Contains(full, StringComparer.Ordinal)) return false;
        (identity with { ProjectFolders = folders.Where(f => f != full).ToList() is { Count: > 0 } left ? left : null }).Save(config);
        return true;
    }
}
