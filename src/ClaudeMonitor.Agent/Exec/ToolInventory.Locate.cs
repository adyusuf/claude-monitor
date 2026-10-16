using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

public static partial class ToolInventory
{
    /// <summary>
    /// The real path of the tool the inventory would report under this name: the first candidate that exists, with a <c>*</c> segment expanded
    /// (the newest folder wins) and links resolved. A script is started by exactly this file, after the guard checked that it is trusted
    /// (ADR-0008 §3c), so what the API saw in the inventory and what the executor starts are the same file.
    /// </summary>
    public static string? RealPath(string name, string os, string userProfile, Func<string, bool>? exists = null, Func<string, string?>? resolve = null,
        Func<string, IEnumerable<string>>? subfolders = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var candidate in Candidates(os, userProfile).Where(c => c.Name == name))
        {
            var path = Expand(candidate.Path, subfolders ?? Subfolders).FirstOrDefault(exists ?? File.Exists);
            if (path is not null && (resolve ?? Resolve)(path) is { Length: > 0 } real) return real;
        }

        return null;
    }
}
