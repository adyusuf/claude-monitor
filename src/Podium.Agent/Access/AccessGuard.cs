using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// Holds a run to its access layers on real paths (ADR-0020 §4). Each layer's folders and applications are resolved to real paths first (an allowed
/// folder or application that does not resolve, or a folder that resolves to a forbidden root, is dropped, so a list that named only such entries
/// allows nothing; a denied folder keeps its text).
/// The run's paths are then matched by identity as well as by text: an ancestor of a path that is one of a layer's folders under another name (a bind
/// mount, a firmlink, another case or Unicode form) counts as that folder, and a name that equals a folder's but is another folder cannot be judged.
/// A missing path is judged by its deepest existing ancestor. The lexical rule is <see cref="AccessPolicyMath.Evaluate"/>. Whatever cannot be read
/// is refused as <see cref="AccessErrors.Unresolved"/>.
/// </summary>
internal static class AccessGuard
{
    public static AccessRefusal? Check(IReadOnlyList<AccessLayer> layers, AccessCheck check, string os)
    {
        if (layers.Count == 0) return null;
        if (!OsKinds.All.Contains(os)) return Refuse(AccessErrors.Unresolved, layers[0]);
        if (check.Shell || check.Script) return AccessPolicyMath.Evaluate(layers, check, os); // no folder or application limit applies to them
        try
        {
            var resolved = layers.Select(layer => new Resolved(layer, os)).ToList();
            foreach (var path in (check.Cwd is null ? [] : new[] { check.Cwd }).Concat(check.Paths ?? []))
            {
                if (Alias(resolved, path, os) is { } refused) return refused;
            }

            List<AccessLayer> final = [.. resolved.Select(r => r.ToLayer())];
            var sha = check.Executable is { } exe && AccessPolicyMath.NeedsHash(final, exe, os) ? ExecutableHashes.Of(exe) : null;
            return AccessPolicyMath.Evaluate(final, check with { ExecutableSha256 = sha }, os);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse(AccessErrors.Unresolved, layers[0]);
        }
    }

    /// <summary>Adds the other names of a layer's folders that the path's ancestors carry; a refusal when an ancestor cannot be read or is ambiguous.</summary>
    private static AccessRefusal? Alias(List<Resolved> layers, string path, string os)
    {
        var limited = layers.FirstOrDefault(r => r.Allowed is not null || r.Denied.Count > 0);
        if (limited is null) return null;
        var comparison = os == OsKinds.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var ancestor in PathIdentity.Ancestors(path))
        {
            if (PathIdentity.Of(ancestor) is not { } id) return Refuse(AccessErrors.Unresolved, limited.Layer);
            foreach (var layer in layers)
            {
                foreach (var (folder, folderId) in layer.AllowedIds)
                {
                    var sameName = string.Equals(ancestor, folder, comparison);
                    if (sameName && folderId is { } other && other != id) return Refuse(AccessErrors.Unresolved, layer.Layer);
                    // Never through a root: a bind mount of "/" must not make everything allowed.
                    if (!sameName && folderId == id && !GrantMatcher.IsForbiddenRoot(ancestor, os)) layer.Allowed!.Add(ancestor);
                }

                foreach (var (folder, folderId) in layer.DeniedIds)
                {
                    if (folderId == id && !string.Equals(ancestor, folder, comparison)) layer.Denied.Add(ancestor);
                }
            }
        }

        return null;
    }

    private static AccessRefusal Refuse(string code, AccessLayer layer) => new(code, layer.Kind, layer.Version);

    /// <summary>One layer with its lists on real paths and the identities of its folders.</summary>
    private sealed class Resolved
    {
        public Resolved(AccessLayer layer, string os)
        {
            Layer = layer;
            // An allowed folder whose real path is a forbidden root (a link to "/" or a home root) is dropped: the list then allows less, never all.
            Allowed = layer.AllowedFolders?.Select(f => PathIdentity.Resolve(f, os, allowMissing: false)).OfType<string>()
                .Where(f => Directory.Exists(f) && !GrantMatcher.IsForbiddenRoot(f, os)).Distinct(StringComparer.Ordinal).ToList();
            Denied = [.. layer.DeniedFolders.Select(f => PathIdentity.Resolve(f, os, allowMissing: true) ?? f).Distinct(StringComparer.Ordinal)];
            Apps = layer.Applications?.Select(a => PathIdentity.Resolve(a.Path, os, allowMissing: false) is { } real && File.Exists(real) ? a with { Path = real } : null)
                .OfType<AccessApp>().ToList();
            AllowedIds = [.. (Allowed ?? []).Select(f => (f, PathIdentity.Of(f)))];
            DeniedIds = [.. Denied.Where(f => Directory.Exists(f) || File.Exists(f)).Select(f => (f, PathIdentity.Of(f)))];
        }

        public AccessLayer Layer { get; }

        public List<string>? Allowed { get; }

        public List<string> Denied { get; }

        public List<AccessApp>? Apps { get; }

        public List<(string Folder, FileId? Id)> AllowedIds { get; }

        public List<(string Folder, FileId? Id)> DeniedIds { get; }

        public AccessLayer ToLayer() => Layer with { AllowedFolders = Allowed, DeniedFolders = Denied, Applications = Apps };
    }
}
