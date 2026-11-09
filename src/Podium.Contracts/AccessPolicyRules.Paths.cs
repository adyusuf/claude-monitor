using System.Text;

namespace Podium.Contracts;

// The path half of AccessPolicyRules: what a valid path is per OS, and the canonical form of one path.
public static partial class AccessPolicyRules
{
    // Characters Windows refuses in a file name; ":" is checked separately because the drive has one.
    private const string WindowsRefused = "*?\"<>|";

    /// <summary>
    /// An absolute folder or file path for the OS: at most 1024 characters, no control, bidirectional or zero-width character, no empty, "." or ".."
    /// segment, no trailing separator except the root. Linux and macOS start with "/"; Windows only "X:\" (no UNC, no \\?\ or \\.\), with no ":" after
    /// the drive, none of *?"&lt;&gt;|, no "/", no segment ending in a dot or a space and no 8.3 short name ("~" and a digit).
    /// </summary>
    public static bool IsValidPath(string? path, string os)
    {
        if (path is not { Length: >= 1 and <= MaxPathLength } || !GrantTemplateRules.IsKnownOs(os) || !IsWellFormed(path)
            || path.Any(c => char.IsControl(c) || TextSafety.IsHidden(c)))
        {
            return false;
        }

        var windows = GrantTemplateRules.IsWindows(os);
        string rest;
        if (windows)
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            {
                return false;
            }

            rest = path[3..];
            if (rest.Any(c => c is ':' or '/' || WindowsRefused.Contains(c)))
            {
                return false;
            }
        }
        else
        {
            if (path[0] != '/')
            {
                return false;
            }

            rest = path[1..];
        }

        return rest.Length == 0 || rest.Split(GrantTemplateRules.Separator(os)).All(segment => IsValidSegment(segment, windows));
    }

    private static bool IsValidSegment(string segment, bool windows)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            return false;
        }

        if (!windows)
        {
            return true;
        }

        if (segment[^1] is '.' or ' ')
        {
            return false;
        }

        for (var i = 0; i + 1 < segment.Length; i++)
        {
            if (segment[i] == '~' && char.IsAsciiDigit(segment[i + 1]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One path in canonical form (see <see cref="Canonical"/>); null stays null.</summary>
    public static string? CanonicalPath(string? path, string os)
    {
        if (path is null)
        {
            return null;
        }

        var text = Nfc(path);
        var separator = GrantTemplateRules.Separator(os);
        var root = text.Length > 0 && text[0] == '/' ? 1 : 0;
        if (GrantTemplateRules.IsWindows(os))
        {
            text = text.Replace('/', '\\');
            if (text.Length >= 2 && text[1] == ':' && char.IsAsciiLetterLower(text[0]))
            {
                text = char.ToUpperInvariant(text[0]) + text[1..];
            }

            root = text.Length >= 3 && text[1] == ':' && text[2] == '\\' ? 3 : 0;
        }

        while (text.Length > Math.Max(root, 1) && text[^1] == separator)
        {
            text = text[..^1];
        }

        return text;
    }

    // A string with an unpaired surrogate has no NFC form (Normalize would throw); it is left as it is and Validate refuses it.
    private static string Nfc(string text) => IsWellFormed(text) ? text.Normalize(NormalizationForm.FormC) : text;

    private static string AsciiLower(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = char.IsAsciiLetterUpper(source[i]) ? (char)(source[i] + ('a' - 'A')) : source[i];
        }
    });

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
