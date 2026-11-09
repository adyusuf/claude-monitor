namespace Podium.Contracts;

/// <summary>
/// One layer a run is checked against (ADR-0020 §4): the machine's ceiling, a project's policy or the default. Kind is one of <see cref="AccessRefusedBy"/>
/// and Version the policy's version (null for the ceiling and the default). AllowedFolders null means no folder limit; an empty list allows no folder.
/// Applications null means no application limit. Every path is already resolved to a real path by the agent.
/// </summary>
public sealed record AccessLayer(string Kind, int? Version, IReadOnlyList<string>? AllowedFolders, IReadOnlyList<string> DeniedFolders,
    IReadOnlyList<AccessApp>? Applications, bool Shell);

/// <summary>
/// What a run asks of the layers, on real paths: its working directory (null: the work folder), every path a grant's placeholder filled, its
/// executable and that file's SHA-256 (lowercase hex; null when it was not computed), and whether it is a shell or a script run.
/// </summary>
public sealed record AccessCheck(string? Cwd, IReadOnlyList<string> Paths, string? Executable, string? ExecutableSha256, bool Shell, bool Script);

/// <summary>Why the layers refused a run: an <see cref="AccessErrors"/> code, the layer that refused it and that layer's version.</summary>
public sealed record AccessRefusal(string Code, string RefusedBy, int? Version);

/// <summary>
/// The lexical half of access enforcement, shared by the API (which shows and ties approval rules to a policy) and the agent (which resolves real paths
/// first, then asks here). Pure: no file system, the OS is a parameter, separators and case follow it (case-insensitive on macOS and Windows).
/// </summary>
public static class AccessPolicyMath
{
    /// <summary>True when path is the folder or below it, at a separator boundary.</summary>
    public static bool IsUnder(string? path, string? folder, string os) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(folder) && GrantTemplateRules.IsKnownOs(os)
        && (string.Equals(path, folder, GrantTemplateRules.PathComparison(os)) || GrantTemplateRules.IsUnder(path, folder, os));

    public static bool IsUnderAny(string? path, IEnumerable<string> folders, string os) => folders.Any(folder => IsUnder(path, folder, os));

    /// <summary>True when path is under one of the allowed folders and under none of the denied ones; an empty allowed list allows nothing.</summary>
    public static bool IsInside(string? path, IReadOnlyList<string> allowed, IReadOnlyList<string> denied, string os) =>
        IsUnderAny(path, allowed, os) && !IsUnderAny(path, denied, os);

    /// <summary>The ceiling as a layer: an empty allowed list or application list is no limit there; shell only when the ceiling allows it.</summary>
    public static AccessLayer Ceiling(IReadOnlyList<string> allowed, IReadOnlyList<string> denied, IReadOnlyList<AccessApp> applications, bool shell) =>
        new(AccessRefusedBy.Ceiling, null, allowed.Count > 0 ? allowed : null, denied, applications.Count > 0 ? applications : null, shell);

    /// <summary>
    /// A policy's section for the machine's OS as a layer. Its allowed folders are the section's plus, when the policy includes it, the project's folders
    /// on this machine; they limit as soon as either is asked for, even when no project folder is known. A cleared policy is not a layer: the default applies.
    /// </summary>
    public static AccessLayer Policy(int version, AccessOsRules rules, bool includeProjectFolder, IReadOnlyList<string> projectFolders, bool shell)
    {
        ArgumentNullException.ThrowIfNull(rules);
        IReadOnlyList<string>? allowed = includeProjectFolder ? [.. rules.AllowedFolders, .. projectFolders]
            : rules.AllowedFolders.Count > 0 ? rules.AllowedFolders : null;
        return new AccessLayer(AccessRefusedBy.Policy, version, allowed, rules.DeniedFolders, rules.Applications.Count > 0 ? rules.Applications : null, shell);
    }

    /// <summary>The default: the project's folders on this machine only (none known: no folder), any application, no shell and no script.</summary>
    public static AccessLayer Default(IReadOnlyList<string> projectFolders) => new(AccessRefusedBy.Default, null, projectFolders, [], null, false);

    /// <summary>
    /// Null when every layer allows the run, else the first refusal in layer order. A shell or script run needs every layer to allow shell, and then
    /// meets no folder or application limit (a shell can start anything and go anywhere). An argv run's working directory and every placeholder path
    /// must be under each layer's allowed folders and under none of its denied ones, and its executable on each layer's application list.
    /// </summary>
    public static AccessRefusal? Evaluate(IReadOnlyList<AccessLayer> layers, AccessCheck check, string os)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(check);
        if (layers.Count == 0)
        {
            return null;
        }

        if (!GrantTemplateRules.IsKnownOs(os))
        {
            return Refuse(AccessErrors.Unresolved, layers[0]);
        }

        if (check.Shell || check.Script)
        {
            var closed = layers.FirstOrDefault(layer => !layer.Shell);
            return closed is null ? null : Refuse(check.Script ? AccessErrors.ScriptRefused : AccessErrors.ShellRefused, closed);
        }

        if (check.Cwd is not null && Folder(layers, check.Cwd, AccessErrors.CwdNotAllowed, AccessErrors.CwdDenied, os) is { } cwd)
        {
            return cwd;
        }

        foreach (var path in check.Paths ?? [])
        {
            if (Folder(layers, path, AccessErrors.PathNotAllowed, AccessErrors.PathDenied, os) is { } refused)
            {
                return refused;
            }
        }

        return Executable(layers, check.Executable, check.ExecutableSha256, os);
    }

    /// <summary>True when some layer allows the executable only with a SHA-256, so the agent must hash the file before <see cref="Evaluate"/>.</summary>
    public static bool NeedsHash(IReadOnlyList<AccessLayer> layers, string? executable, string os) =>
        layers.Any(layer => layer.Applications is { } apps && Matching(apps, executable, os) is { Count: > 0 } matches && matches.All(a => a.Sha256 is not null));

    private static AccessRefusal? Folder(IReadOnlyList<AccessLayer> layers, string path, string notAllowed, string denied, string os)
    {
        foreach (var layer in layers)
        {
            if (IsUnderAny(path, layer.DeniedFolders, os))
            {
                return Refuse(denied, layer);
            }

            if (layer.AllowedFolders is { } allowed && !IsUnderAny(path, allowed, os))
            {
                return Refuse(notAllowed, layer);
            }
        }

        return null;
    }

    private static AccessRefusal? Executable(IReadOnlyList<AccessLayer> layers, string? executable, string? sha256, string os)
    {
        foreach (var layer in layers)
        {
            if (layer.Applications is not { } apps)
            {
                continue;
            }

            var matches = Matching(apps, executable, os);
            if (matches.Count == 0)
            {
                return Refuse(AccessErrors.ExeNotAllowed, layer);
            }

            if (!matches.Any(a => a.Sha256 is null || string.Equals(a.Sha256, sha256, StringComparison.Ordinal)))
            {
                return Refuse(AccessErrors.ExeHash, layer);
            }
        }

        return null;
    }

    private static List<AccessApp> Matching(IReadOnlyList<AccessApp> apps, string? executable, string os) =>
        string.IsNullOrEmpty(executable) ? [] : apps.Where(a => string.Equals(a.Path, executable, GrantTemplateRules.PathComparison(os))).ToList();

    private static AccessRefusal Refuse(string code, AccessLayer layer) => new(code, layer.Kind, layer.Version);
}
