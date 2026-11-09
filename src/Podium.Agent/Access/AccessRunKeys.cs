using Podium.Agent.Capture;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// The repositories a run touches (ADR-0020 §4), read from the machine, not from the run's label: the run's ProjectKey is chosen by the requesting
/// session, so the checkout around its real working directory and around every real path a grant's placeholders filled count as well, and every
/// stored policy that covers any of them applies. A label that names another repository than the working directory's checkout is refused; a path
/// inside a checkout whose key cannot be read cannot be judged and is refused.
/// </summary>
public static class AccessRunKeys
{
    /// <summary>The keys the run touches, or the refusal when they cannot be judged or the label disagrees with the working directory.</summary>
    public static (IReadOnlyList<string> Keys, AccessRefusal? Refusal) Of(string? label, string? cwd, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(label)) keys.Add(label);
        if (cwd is not null)
        {
            var (judged, key) = KeyOf(cwd);
            if (!judged) return ([], Refuse(AccessErrors.Unresolved));
            if (key is not null && !string.IsNullOrEmpty(label) && !RepoKeys.Same(label, key)) return ([], Refuse(AccessErrors.CwdNotAllowed));
            if (key is not null) keys.Add(key);
        }

        foreach (var path in paths)
        {
            var (judged, key) = KeyOf(path);
            if (!judged) return ([], Refuse(AccessErrors.Unresolved));
            if (key is not null) keys.Add(key);
        }

        return ([.. keys.Distinct(RepoKeys.Comparer)], null);
    }

    /// <summary>
    /// The key of the checkout a real path lies in (judged by its deepest existing part): (true, null) outside any checkout, (false, null) when the
    /// checkout's key cannot be read.
    /// </summary>
    public static (bool Judged, string? Key) KeyOf(string realPath)
    {
        try
        {
            var existing = PathIdentity.DeepestExisting(realPath);
            if (existing is null) return (false, null);
            var root = ProjectInfo.RootOf(existing);
            if (root is null) return (true, null);
            return ProjectInfo.Resolve(root) is { } project ? (true, project.Key) : (false, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (false, null); // a checkout that cannot be read cannot be placed: refused, never "no policy"
        }
    }

    private static AccessRefusal Refuse(string code) => new(code, AccessRefusedBy.Policy, null);
}
