using Podium.Agent.Capture;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// The repositories a run touches (ADR-0020 §4), read from the machine, not from the run's label: the run's ProjectKey is chosen by the requesting
/// session, so every checkout around its real working directory and around every real path a grant's placeholders filled counts as well (a submodule
/// or a nested clone never hides the repository around it), and every stored policy that covers any of them applies. A label that names none of the
/// working directory's enclosing checkouts is reported as a mismatch (refused where a layer beyond the ceiling applies); a path inside a checkout
/// whose key cannot be read cannot be judged and is refused.
/// </summary>
public static class AccessRunKeys
{
    /// <summary>The keys the run touches and whether the label names none of the working directory's checkouts; a refusal when they cannot be judged.</summary>
    public static (IReadOnlyList<string> Keys, AccessRefusal? Refusal, bool LabelMismatch) Of(string? label, string? cwd, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var keys = new List<string>();
        var mismatch = false;
        if (!string.IsNullOrEmpty(label)) keys.Add(label);
        if (cwd is not null)
        {
            var (judged, around) = KeysOf(cwd);
            if (!judged) return ([], Refuse(AccessErrors.Unresolved), false);
            mismatch = around.Count > 0 && !string.IsNullOrEmpty(label) && !around.Any(key => RepoKeys.Same(label, key));
            keys.AddRange(around);
        }

        foreach (var path in paths)
        {
            var (judged, around) = KeysOf(path);
            if (!judged) return ([], Refuse(AccessErrors.Unresolved), false);
            keys.AddRange(around);
        }

        return ([.. keys.Distinct(RepoKeys.Comparer)], null, mismatch);
    }

    /// <summary>The refusal of a run whose label names none of its working directory's checkouts.</summary>
    public static AccessRefusal Mismatch() => Refuse(AccessErrors.CwdNotAllowed);

    /// <summary>
    /// The keys of every checkout a real path lies in (judged by its deepest existing part), innermost first, up to the root: empty outside any
    /// checkout, not judged when one of them cannot be read.
    /// </summary>
    public static (bool Judged, IReadOnlyList<string> Keys) KeysOf(string realPath)
    {
        try
        {
            var existing = PathIdentity.DeepestExisting(realPath);
            if (existing is null) return (false, []);
            var keys = new List<string>();
            for (var root = ProjectInfo.RootOf(existing); root is not null; root = Path.GetDirectoryName(root) is { } parent ? ProjectInfo.RootOf(parent) : null)
            {
                if (ProjectInfo.Resolve(root) is not { } project) return (false, []);
                keys.Add(project.Key);
            }

            return (true, keys);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (false, []); // a checkout that cannot be read cannot be placed: refused, never "no policy"
        }
    }

    private static AccessRefusal Refuse(string code) => new(code, AccessRefusedBy.Policy, null);
}
