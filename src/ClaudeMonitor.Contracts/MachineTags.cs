using System.Text.RegularExpressions;

namespace ClaudeMonitor.Contracts;

/// <summary>
/// Tags and the admin note of a machine (ADR-0008 §3a′). A tag is a <c>key=value</c> pair an admin types: at most
/// <see cref="Max"/> per machine, keys and values in <c>[A-Za-z0-9._-]{1,32}</c>. The note is free text for the runbook: at most
/// <see cref="NoteMax"/> characters, no control characters other than line breaks and tabs. Both are untrusted text when Claude reads them.
/// </summary>
public static partial class MachineTags
{
    public const int Max = 10;
    public const int PartMax = 32;
    public const int NoteMax = 2000;

    public static bool IsPart(string? text) => text is { Length: > 0 and <= PartMax } && Part().IsMatch(text);

    /// <summary>True when every pair is valid and there are at most <see cref="Max"/> of them. A null or empty set clears the tags.</summary>
    public static bool IsValid(IReadOnlyDictionary<string, string>? tags) =>
        tags is null || (tags.Count <= Max && tags.All(t => IsPart(t.Key) && IsPart(t.Value)));

    /// <summary>True for text of at most <see cref="NoteMax"/> characters without control characters (a line break and a tab pass). Null clears the note.</summary>
    public static bool IsNote(string? note) =>
        note is null || (note.Length <= NoteMax && note.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'));

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Part();
}
