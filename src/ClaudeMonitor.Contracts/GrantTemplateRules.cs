namespace ClaudeMonitor.Contracts;

// The lexical rules behind GrantMatcher (ADR-0004, "Grants: full templates, checked twice"). Pure: no file system and no
// OperatingSystem.Is*; the target's OS is always a parameter.

internal static class GrantTemplateRules
{
    public const int MaxElements = 64;
    public const int MaxElementLength = 1024;
    public const int MinRootDepth = 2;
    public const string WindowsExecutableExtension = ".exe";

    // Shell and Windows-parser metacharacters; a space is checked separately because argv[0] and cwd may contain one.
    private const string MetaChars = "\"'`$%~*?[]^!&|<>;{}";
    private const char UnixSeparator = '/';
    private const char WindowsSeparator = '\\';

    private static readonly HashSet<string> NeverGrantable = new HashSet<string>(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "ksh", "fish", "csh", "tcsh", "ash", "busybox", "toybox",
        "env", "sudo", "su", "doas", "pkexec", "runuser", "setsid", "nohup", "xargs", "nice", "timeout", "ssh",
        "perl", "ruby", "node", "nodejs", "deno", "bun", "php", "lua", "tclsh", "awk", "gawk", "mawk", "nawk", "sed",
        "vi", "vim", "ed", "emacs", "less", "more", "man",
        "pwsh", "cmd", "wsl", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "forfiles", "schtasks",
        "sc", "certutil", "bitsadmin", "wmic", "conhost", "explorer", "runas",
        "osascript", "open", "launchctl",
        "dotnet", "java", "docker", "kubectl", "git",
    };

    private static readonly string[] NeverGrantablePrefixes = ["python", "pypy", "powershell"];

    private static readonly HashSet<string> WindowsDeviceNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
    };

    // The first path segment of roots that are refused together with everything below them (compared per OS).
    private static readonly string[] ForbiddenUnixTop = ["etc", "root", "proc", "sys", "dev"];
    private const string MacOsPrivate = "private";
    private const string MacOsPrivateEtc = "etc";
    private const string WindowsTop = "Windows";

    public static bool IsKnownOs(string? os) => os is not null && OsKinds.All.Contains(os);

    public static bool IsWindows(string os) => os == OsKinds.Windows;

    public static char Separator(string os) => IsWindows(os) ? WindowsSeparator : UnixSeparator;

    // Linux paths are case-sensitive; macOS and Windows file systems are case-insensitive by default.
    public static StringComparison PathComparison(string os) => os == OsKinds.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static bool IsPlainLiteral(string text, string os)
    {
        if (text.Length is 0 or > MaxElementLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < ' ' or > '~' || (IsWindows(os) && c == '"'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The segments of a clean absolute path, or null when it is not one: ASCII only, no control characters, no
    /// metacharacters, no "." or ".." segment, no doubled separator, and on Windows a drive path without a stream,
    /// trailing dot or space, or device name. An empty array is a root.
    /// </summary>
    public static string[]? PathSegments(string? path, string os, bool allowSpace, bool allowTrailingSeparator)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxElementLength)
        {
            return null;
        }

        var windows = IsWindows(os);
        var separator = Separator(os);
        string rest;
        if (windows)
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != WindowsSeparator)
            {
                return null;
            }

            rest = path[3..];
        }
        else
        {
            if (path[0] != UnixSeparator)
            {
                return null;
            }

            rest = path[1..];
        }

        if (rest.Length == 0)
        {
            return [];
        }

        if (allowTrailingSeparator && rest[^1] == separator)
        {
            rest = rest[..^1];
            if (rest.Length == 0)
            {
                return null;
            }
        }

        var segments = rest.Split(separator);
        foreach (var segment in segments)
        {
            if (!IsCleanSegment(segment, windows, allowSpace))
            {
                return null;
            }
        }

        return segments;
    }

    private static bool IsCleanSegment(string segment, bool windows, bool allowSpace)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment[0] == ' ' || segment[^1] == ' ')
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (c is < ' ' or > '~' || (c == ' ' && !allowSpace) || MetaChars.Contains(c) || c == WindowsSeparator)
            {
                return false;
            }

            if (windows && (c == ':' || c == UnixSeparator))
            {
                return false;
            }
        }

        return !windows || (segment[^1] != '.' && !IsWindowsDeviceName(segment));
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ').ToUpperInvariant();
        if (WindowsDeviceNames.Contains(stem))
        {
            return true;
        }

        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && char.IsAsciiDigit(stem[3]);
    }

    public static bool IsAbsoluteExecutable(string? argv0, string os)
    {
        var segments = PathSegments(argv0, os, allowSpace: true, allowTrailingSeparator: false);
        if (segments is null || segments.Length == 0)
        {
            return false;
        }

        return !IsWindows(os) || (segments[^1].Length > WindowsExecutableExtension.Length
            && segments[^1].EndsWith(WindowsExecutableExtension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUnder(string path, string root, string os)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
        {
            return false;
        }

        var separator = Separator(os);
        var comparison = PathComparison(os);
        var trimmed = root.Length > 1 && root[^1] == separator ? root[..^1] : root;
        if (trimmed[^1] == separator)
        {
            return path.Length > trimmed.Length && path.StartsWith(trimmed, comparison);
        }

        return path.Equals(trimmed, comparison) || (path.Length > trimmed.Length && path.StartsWith(trimmed, comparison) && path[trimmed.Length] == separator);
    }

    /// <summary>Structural only (no character rules), so it also judges a resolved real path.</summary>
    public static bool IsForbiddenRoot(string? root, string os)
    {
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var windows = IsWindows(os);
        var parts = root.Split(Separator(os), StringSplitOptions.RemoveEmptyEntries);
        var segments = windows && parts.Length > 0 && parts[0].EndsWith(':') ? parts[1..] : parts;
        if (segments.Length < MinRootDepth)
        {
            return true;
        }

        var comparison = PathComparison(os);
        if (windows)
        {
            return segments[0].Equals(WindowsTop, comparison);
        }

        if (segments[0].Equals(MacOsPrivate, comparison) && segments[1].Equals(MacOsPrivateEtc, comparison))
        {
            return true;
        }

        return ForbiddenUnixTop.Any(top => segments[0].Equals(top, comparison));
    }

    /// <summary>The program name the never-grantable list is matched on: base name, lower case, Windows extension and trailing version removed.</summary>
    public static string ProgramKey(string argv0, string os)
    {
        var start = Math.Max(argv0.LastIndexOf(UnixSeparator), argv0.LastIndexOf(WindowsSeparator)) + 1;
        var name = argv0[start..].ToLowerInvariant();
        if (IsWindows(os))
        {
            name = name.TrimEnd('.', ' ');
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                name = name[..dot];
            }
        }

        return StripVersion(name);
    }

    // python3.12 -> python, perl5.30 -> perl, bash-5.2 -> bash; a name that is only digits is kept as it is.
    private static string StripVersion(string name)
    {
        var end = name.Length;
        while (end > 0 && (char.IsAsciiDigit(name[end - 1]) || (name[end - 1] == '.' && end > 1 && char.IsAsciiDigit(name[end - 2]))))
        {
            end--;
        }

        if (end > 0 && end < name.Length && name[end - 1] is '-' or '_')
        {
            end--;
        }

        return end == 0 ? name : name[..end];
    }

    public static bool IsNeverGrantable(string argv0, string os)
    {
        var key = ProgramKey(argv0, os);
        return NeverGrantable.Contains(key) || NeverGrantablePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
    }
}
