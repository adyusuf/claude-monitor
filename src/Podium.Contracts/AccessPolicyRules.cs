namespace Podium.Contracts;

/// <summary>
/// The limits and the canonical form of an access policy document (ADR-0020 §4), shared by the API (which refuses what breaks them) and the agent (which
/// falls back to the default); the web mirrors both. Pure: the OS is a parameter and nothing touches the file system. <see cref="Validate"/> judges a
/// document as it is; <see cref="Canonical"/> is what the browser sends and signs, and the API refuses a body that is not already canonical.
/// </summary>
public static partial class AccessPolicyRules
{
    public const int Format = 1;
    public const int MaxProjectKeys = 20;
    public const int MaxProjectKeyLength = 200;
    public const int MaxFolders = 32;
    public const int MaxApplications = 32;
    public const int MaxPathLength = 1024;
    public const int MaxEncodedBytes = 65_536;
    public const int Sha256HexLength = 64;

    private static readonly AccessOsRules Empty = new([], [], []);

    /// <summary>What a project without a saved policy shows: its own folder on each machine, no other folder, any application, no shell.</summary>
    public static AccessPolicyDocument DefaultDocument(IReadOnlyList<string> projectKeys) =>
        new(Format, projectKeys, IncludeProjectFolder: true, Empty, Empty, Empty, Shell: false, Cleared: false);

    /// <summary>The section of the document for one OS (<see cref="OsKinds"/>), or null for an unknown OS.</summary>
    public static AccessOsRules? Section(AccessPolicyDocument document, string os) => os switch
    {
        OsKinds.Linux => document.Linux,
        OsKinds.MacOs => document.MacOs,
        OsKinds.Windows => document.Windows,
        _ => null,
    };

    /// <summary>Null when the document is valid as it is, else an <see cref="AccessPolicyErrors"/> code.</summary>
    public static string? Validate(AccessPolicyDocument? document)
    {
        if (!IsComplete(document))
        {
            return AccessPolicyErrors.Malformed;
        }

        if (document!.Format != Format)
        {
            return AccessPolicyErrors.Format;
        }

        if (document.ProjectKeys.Count is < 1 or > MaxProjectKeys || !document.ProjectKeys.All(IsValidKey))
        {
            return AccessPolicyErrors.Keys;
        }

        foreach (var os in new[] { OsKinds.Linux, OsKinds.MacOs, OsKinds.Windows })
        {
            var error = ValidateSection(Section(document, os)!, os);
            if (error is not null)
            {
                return error;
            }
        }

        if (document.Cleared && (document.Shell || HasEntries(document.Linux) || HasEntries(document.MacOs) || HasEntries(document.Windows)))
        {
            return AccessPolicyErrors.Cleared;
        }

        return AccessPolicySigning.Encode(document).Length > MaxEncodedBytes ? AccessPolicyErrors.TooLarge : null;
    }

    private static bool IsComplete(AccessPolicyDocument? document) =>
        document?.ProjectKeys is not null && document.ProjectKeys.All(k => k is not null)
        && IsComplete(document.Linux) && IsComplete(document.MacOs) && IsComplete(document.Windows);

    private static bool IsComplete(AccessOsRules? rules) =>
        rules?.AllowedFolders is not null && rules.DeniedFolders is not null && rules.Applications is not null
        && rules.AllowedFolders.All(p => p is not null) && rules.DeniedFolders.All(p => p is not null)
        && rules.Applications.All(a => a?.Path is not null);

    private static bool HasEntries(AccessOsRules rules) => rules.AllowedFolders.Count + rules.DeniedFolders.Count + rules.Applications.Count > 0;

    private static string? ValidateSection(AccessOsRules rules, string os)
    {
        if (rules.AllowedFolders.Count > MaxFolders || rules.DeniedFolders.Count > MaxFolders || rules.Applications.Count > MaxApplications)
        {
            return AccessPolicyErrors.TooMany;
        }

        if (!rules.AllowedFolders.All(p => IsValidPath(p, os)) || !rules.DeniedFolders.All(p => IsValidPath(p, os)))
        {
            return AccessPolicyErrors.Path;
        }

        if (rules.AllowedFolders.Any(p => GrantTemplateRules.IsForbiddenRoot(p, os)))
        {
            return AccessPolicyErrors.ForbiddenRoot;
        }

        if (!rules.Applications.All(a => IsValidPath(a.Path, os) && GrantMatcher.IsAbsoluteExecutable(a.Path, os)))
        {
            return AccessPolicyErrors.App;
        }

        return rules.Applications.All(a => a.Sha256 is null || IsSha256(a.Sha256)) ? null : AccessPolicyErrors.Sha256;
    }

    /// <summary>A repo key: 1 to 200 characters, none of them a control, bidirectional or zero-width character.</summary>
    public static bool IsValidKey(string? key) =>
        key is { Length: >= 1 and <= MaxProjectKeyLength } && IsWellFormed(key) && !key.Any(c => char.IsControl(c) || TextSafety.IsHidden(c));

    /// <summary>Lowercase hex, 64 characters.</summary>
    public static bool IsSha256(string? text) => text is { Length: Sha256HexLength } && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>
    /// The canonical form: every text NFC; on Windows "/" becomes "\" and the drive letter upper case; no trailing separator except a root; SHA-256 in
    /// lower case; every list without exact duplicates and sorted ordinally (UTF-16), project keys included. No case-folded dedupe, on purpose (.NET
    /// and the browser fold some letters differently). A missing list becomes empty and a missing entry is dropped.
    /// </summary>
    public static AccessPolicyDocument Canonical(AccessPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with
        {
            ProjectKeys = Sorted((document.ProjectKeys ?? []).OfType<string>().Select(Nfc)),
            Linux = Canonical(document.Linux, OsKinds.Linux),
            MacOs = Canonical(document.MacOs, OsKinds.MacOs),
            Windows = Canonical(document.Windows, OsKinds.Windows),
        };
    }

    /// <summary>True when the document is exactly its own <see cref="Canonical"/> form.</summary>
    public static bool IsCanonical(AccessPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsComplete(document))
        {
            return false;
        }

        var canonical = Canonical(document);
        return document.ProjectKeys.SequenceEqual(canonical.ProjectKeys, StringComparer.Ordinal)
            && SameRules(document.Linux, canonical.Linux) && SameRules(document.MacOs, canonical.MacOs) && SameRules(document.Windows, canonical.Windows);
    }

    private static bool SameRules(AccessOsRules a, AccessOsRules b) =>
        a.AllowedFolders.SequenceEqual(b.AllowedFolders, StringComparer.Ordinal) && a.DeniedFolders.SequenceEqual(b.DeniedFolders, StringComparer.Ordinal)
        && a.Applications.SequenceEqual(b.Applications);

    private static AccessOsRules Canonical(AccessOsRules? rules, string os)
    {
        if (rules is null)
        {
            return Empty;
        }

        var apps = (rules.Applications ?? [])
            .Where(a => a?.Path is not null)
            .Select(a => new AccessApp(CanonicalPath(a.Path, os)!, a.Sha256 is null ? null : AsciiLower(a.Sha256)))
            .Distinct()
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .ThenBy(a => a.Sha256, StringComparer.Ordinal)
            .ToList();
        return new AccessOsRules(Sorted((rules.AllowedFolders ?? []).OfType<string>().Select(p => CanonicalPath(p, os)!)),
            Sorted((rules.DeniedFolders ?? []).OfType<string>().Select(p => CanonicalPath(p, os)!)), apps);
    }

    private static List<string> Sorted(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
