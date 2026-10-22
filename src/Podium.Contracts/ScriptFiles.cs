namespace Podium.Contracts;

/// <summary>One text file a script run carries (ADR-0010 §2): a relative path below the run's scratch folder and its text.</summary>
public sealed record ScriptFile(string Path, string Text);

/// <summary>
/// The rules for the files and the stdin text of a script run, one definition for the web, the API and the agent: a few small UTF-8 text
/// files at safe relative paths, because the owner approves exactly what they can read. Binary files and archives are not supported.
/// </summary>
public static class ScriptFiles
{
    public const int MaxFiles = 8;
    public const int FileMax = 65_536;
    public const int TotalMax = 262_144;
    public const int StdinMax = 65_536;
    public const int MaxSegments = 4;
    public const int SegmentMax = 64;

    /// <summary>Names the run's own scratch folder uses: the script itself and the temporary folder.</summary>
    private static readonly string[] ReservedFirstSegments = ["tmp"];

    /// <summary>Null when the files and the stdin are acceptable, else <see cref="RemoteErrors.InvalidFiles"/>.</summary>
    public static string? Validate(IReadOnlyList<ScriptFile>? files, string? stdin)
    {
        if (stdin is not null && (stdin.Length > StdinMax || stdin.Contains('\0'))) return RemoteErrors.InvalidFiles;
        if (files is null) return null;
        if (files.Count > MaxFiles) return RemoteErrors.InvalidFiles;
        var total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file?.Path is null || file.Text is null || !IsSafePath(file.Path)) return RemoteErrors.InvalidFiles;
            if (file.Text.Length > FileMax || file.Text.Contains('\0')) return RemoteErrors.InvalidFiles;
            total += file.Text.Length;
            if (total > TotalMax) return RemoteErrors.InvalidFiles;
            if (!seen.Add(file.Path)) return RemoteErrors.InvalidFiles; // the same path twice, or two that differ only in case
            if (seen.Any(other => !ReferenceEquals(other, file.Path) && IsParentOf(other, file.Path))) return RemoteErrors.InvalidFiles; // a file and a folder of one name
        }

        return null;
    }

    /// <summary>
    /// A relative path of at most <see cref="MaxSegments"/> segments of <c>[A-Za-z0-9._-]</c>, none empty, none starting with a dot, none a
    /// <c>script</c> or <c>tmp</c> of the run's own, with <c>/</c> as the only separator (no backslash, no drive, no <c>..</c>).
    /// </summary>
    public static bool IsSafePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxSegments * (SegmentMax + 1)) return false;
        var segments = path.Split('/');
        if (segments.Length > MaxSegments) return false;
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > SegmentMax || segment[0] == '.') return false;
            if (!segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        }

        var first = segments[0];
        if (ReservedFirstSegments.Contains(first, StringComparer.OrdinalIgnoreCase)) return false;
        var stem = first.Contains('.') ? first[..first.IndexOf('.', StringComparison.Ordinal)] : first;
        return !(segments.Length == 1 && stem.Equals(Script, StringComparison.OrdinalIgnoreCase)) && !first.Equals(Script, StringComparison.OrdinalIgnoreCase);
    }

    private const string Script = "script";

    private static bool IsParentOf(string parent, string child) =>
        child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase) || parent.StartsWith(child + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the run carries any file or a stdin text (the hash then uses its second tag).</summary>
    public static bool Any(IReadOnlyList<ScriptFile>? files, string? stdin) => files is { Count: > 0 } || stdin is { Length: > 0 };
}
