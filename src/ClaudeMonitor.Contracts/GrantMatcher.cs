using System.Security.Cryptography;
using System.Text;

namespace ClaudeMonitor.Contracts;

/// <summary>Why a grant template was refused by <see cref="GrantMatcher.Validate"/>.</summary>
public static class GrantErrors
{
    public const string Malformed = "grant_malformed";
    public const string UnknownOs = "unknown_os";
    public const string Argv0 = "argv0_invalid";
    public const string NeverGrantable = "argv0_never_grantable";
    public const string Element = "element_invalid";
    public const string Placeholder = "placeholder_invalid";
    public const string Root = "root_invalid";
    public const string Cwd = "cwd_invalid";
    public const string Timeout = "timeout_invalid";
    public const string ExecOption = "exec_option";
}

/// <summary>A value that filled a <c>{path:ROOT}</c> placeholder, with the root it must stay under.</summary>
public sealed record GrantPathUse(string Value, string Root);

/// <summary>
/// The one grant matcher (ADR-0004, "Grants: full templates, checked twice"). Pure: it never touches the file system
/// and takes the target's OS as a parameter. The API uses it to auto-approve; the target runs the same check and then
/// resolves real paths (ExecGuard). Anything it does not understand is refused.
/// </summary>
public static class GrantMatcher
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSecondsLimit = 3600;

    private const string RunHashTag = "run1";
    private const string GrantHashTag = "grant1";

    /// <summary>Null when the template is valid, else a <see cref="GrantErrors"/> code.</summary>
    public static string? Validate(GrantTemplate grant, string os, bool requestedByClaude)
    {
        if (grant?.Argv is null || grant.Cwd is null)
        {
            return GrantErrors.Malformed;
        }

        if (!GrantTemplateRules.IsKnownOs(os))
        {
            return GrantErrors.UnknownOs;
        }

        var argv = grant.Argv;
        if (argv.Count < 1 || argv.Count > GrantTemplateRules.MaxElements)
        {
            return GrantErrors.Malformed;
        }

        if (!GrantTemplateRules.IsAbsoluteExecutable(argv[0], os))
        {
            return GrantErrors.Argv0;
        }

        if (GrantTemplateRules.IsNeverGrantable(argv[0], os))
        {
            return GrantErrors.NeverGrantable;
        }

        var elementError = ValidateElements(argv, os, requestedByClaude);
        if (elementError is not null)
        {
            return elementError;
        }

        if (GrantTemplateRules.PathSegments(grant.Cwd, os, allowSpace: true, allowTrailingSeparator: false) is null)
        {
            return GrantErrors.Cwd;
        }

        return grant.MaxTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSecondsLimit ? GrantErrors.Timeout : null;
    }

    private static string? ValidateElements(IReadOnlyList<string> argv, string os, bool requestedByClaude)
    {
        var program = GrantTemplateRules.ProgramKey(argv[0], os);
        for (var i = 1; i < argv.Count; i++)
        {
            var element = GrantPlaceholders.Parse(argv[i], os, out var error);
            if (element is null)
            {
                return error;
            }

            if (element.Placeholder?.Kind == PlaceholderKind.Path && IsBadRoot(element.Placeholder.Root, os))
            {
                return GrantErrors.Root;
            }

            if (requestedByClaude && LiteralPieces(element).Any(piece => GrantOptionLint.IsExecOption(piece, program)))
            {
                return GrantErrors.ExecOption;
            }
        }

        return null;
    }

    private static bool IsBadRoot(string? root, string os) =>
        GrantTemplateRules.PathSegments(root, os, allowSpace: false, allowTrailingSeparator: true) is null
        || GrantTemplateRules.IsForbiddenRoot(root, os);

    private static IEnumerable<string> LiteralPieces(TemplateElement element)
    {
        if (element.Prefix.Length > 0)
        {
            yield return element.Prefix;
        }

        if (element.Placeholder?.Choices is { } choices)
        {
            foreach (var choice in choices)
            {
                yield return element.Prefix + choice;
            }
        }
    }

    /// <summary>Lexical match of one call against a template: same length, every element fits, cwd and timeout within the grant.</summary>
    public static bool Matches(GrantTemplate grant, IReadOnlyList<string> argv, string? cwd, int timeoutSeconds, string os) =>
        MatchAll(grant, argv, cwd, timeoutSeconds, os, null);

    /// <summary>The <c>{path:ROOT}</c> values of a call that <see cref="Matches"/> accepted; empty when it does not match.</summary>
    public static IReadOnlyList<GrantPathUse> PathUses(GrantTemplate grant, IReadOnlyList<string> argv, string os)
    {
        var uses = new List<GrantPathUse>();
        return MatchAll(grant, argv, null, MinTimeoutSeconds, os, uses) ? uses : [];
    }

    private static bool MatchAll(GrantTemplate grant, IReadOnlyList<string> argv, string? cwd, int timeoutSeconds, string os,
        List<GrantPathUse>? uses)
    {
        if (grant?.Argv is null || grant.Cwd is null || argv is null || !GrantTemplateRules.IsKnownOs(os))
        {
            return false;
        }

        if (argv.Count == 0 || argv.Count != grant.Argv.Count || timeoutSeconds < MinTimeoutSeconds || timeoutSeconds > grant.MaxTimeoutSeconds)
        {
            return false;
        }

        var comparison = GrantTemplateRules.PathComparison(os);
        if (cwd is not null && !string.Equals(cwd, grant.Cwd, comparison))
        {
            return false;
        }

        if (argv[0] is null || !string.Equals(argv[0], grant.Argv[0], comparison))
        {
            return false;
        }

        for (var i = 1; i < argv.Count; i++)
        {
            if (argv[i] is null || !ElementMatches(grant.Argv[i], argv[i], os, uses))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ElementMatches(string templateElement, string actual, string os, List<GrantPathUse>? uses)
    {
        var element = GrantPlaceholders.Parse(templateElement, os, out _);
        if (element is null)
        {
            return false;
        }

        if (element.Placeholder is null)
        {
            return string.Equals(actual, element.Prefix, StringComparison.Ordinal);
        }

        if (!actual.StartsWith(element.Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = actual[element.Prefix.Length..];
        if (!GrantPlaceholders.ValueMatches(element.Placeholder, value, os))
        {
            return false;
        }

        if (element.Placeholder.Kind == PlaceholderKind.Path && element.Placeholder.Root is not null)
        {
            uses?.Add(new GrantPathUse(value, element.Placeholder.Root));
        }

        return true;
    }

    /// <summary>True for a program on the never-grantable list (shell, launcher, interpreter); a per-call run of one shows a red banner.</summary>
    public static bool IsInterpreter(string argv0, string os) => !string.IsNullOrEmpty(argv0) && GrantTemplateRules.IsNeverGrantable(argv0, os);

    /// <summary>An absolute program path as a template or a per-call run may name it; on Windows a drive path to an .exe.</summary>
    public static bool IsAbsoluteExecutable(string? argv0, string os) =>
        GrantTemplateRules.IsKnownOs(os) && GrantTemplateRules.IsAbsoluteExecutable(argv0, os);

    /// <summary>True when path is root or below it, at a separator boundary; case-insensitive on macOS and Windows only.</summary>
    public static bool IsUnderRoot(string path, string root, string os) =>
        GrantTemplateRules.IsKnownOs(os) && GrantTemplateRules.IsUnder(path, root, os);

    /// <summary>True for a root that may never be granted: shallower than two segments, or /etc, /root, /proc, /sys, /dev, C:\Windows and below.</summary>
    public static bool IsForbiddenRoot(string root, string os) => !GrantTemplateRules.IsKnownOs(os) || GrantTemplateRules.IsForbiddenRoot(root, os);

    /// <summary>Lowercase hex SHA-256 of a length-prefixed encoding of argv, cwd and longest timeout.</summary>
    public static string Hash(GrantTemplate grant)
    {
        using var buffer = new MemoryStream();
        Put(buffer, GrantHashTag);
        PutList(buffer, grant.Argv);
        Put(buffer, grant.Cwd);
        PutInt(buffer, grant.MaxTimeoutSeconds);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Lowercase hex SHA-256 of a length-prefixed encoding of a run; the owner's approval binds to it.</summary>
    public static string RunHash(string mode, IReadOnlyList<string>? argv, string? shellCommand, string? cwd, int timeoutSeconds,
        Guid targetAgentId)
    {
        using var buffer = new MemoryStream();
        Put(buffer, RunHashTag);
        Put(buffer, mode);
        Put(buffer, targetAgentId.ToString("D"));
        PutList(buffer, argv);
        Put(buffer, shellCommand);
        Put(buffer, cwd);
        PutInt(buffer, timeoutSeconds);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    // Every field is "present flag, byte length, bytes", so null and empty differ and no two splits give the same bytes.
    private static void Put(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        var bytes = Encoding.UTF8.GetBytes(value);
        PutInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void PutList(Stream stream, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        PutInt(stream, values.Count);
        foreach (var value in values)
        {
            Put(stream, value);
        }
    }

    private static void PutInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
