using Podium.Agent.Capture;
using Podium.Agent.Storage;

namespace Podium.Agent.Access;

/// <summary>
/// A project's folders on this machine (ADR-0020 §4): the folders listed for it (exec.json "projects" or agent.json) and the one a session of it last
/// worked in, each taken to its checkout root and kept only when that root still IS the project now (its key read again at check time: a folder that
/// was re-cloned elsewhere or forgotten does not count), then resolved to a real path. Never leaves the machine; only a count is reported.
/// </summary>
public static class AccessFolders
{
    public static List<string> For(string projectKey, IReadOnlyList<string>? listed, LocalStore store, string os)
    {
        ArgumentNullException.ThrowIfNull(store);
        var candidates = (listed ?? []).ToList();
        if (store.FolderOf(projectKey) is { } visited) candidates.Add(visited);
        var folders = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (Folder(candidate, projectKey, os) is { } real && !folders.Contains(real, StringComparer.Ordinal)) folders.Add(real);
        }

        return folders;
    }

    private static string? Folder(string candidate, string projectKey, string os)
    {
        try
        {
            var root = ProjectInfo.RootOf(candidate) ?? candidate;
            if (ProjectInfo.Resolve(root)?.Key != projectKey) return null;
            var real = PathIdentity.Resolve(root, os, allowMissing: false);
            return real is not null && Directory.Exists(real) ? real : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // a folder that cannot be read is not one of the project's: fewer folders, never more
        }
    }
}
