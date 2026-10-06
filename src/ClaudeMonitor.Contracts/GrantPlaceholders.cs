using System.Globalization;

namespace ClaudeMonitor.Contracts;

// The placeholders of a grant template and the rules a value must satisfy to fill one (ADR-0005).

internal enum PlaceholderKind
{
    Int,
    Word,
    Path,
    Enum,
}

internal sealed record Placeholder(PlaceholderKind Kind, int Min, int Max, string? Root, IReadOnlyList<string>? Choices);

/// <summary>One argv element of a template: a literal (Placeholder null), or Prefix followed by one placeholder.</summary>
internal sealed record TemplateElement(string Prefix, Placeholder? Placeholder);

internal static class GrantPlaceholders
{
    private const int MaxDigits = 9;
    private const int MaxWordLength = 128;
    private const int MaxChoiceLength = 128;
    private const string WordBody = "word";
    private const string IntPrefix = "int:";
    private const string PathPrefix = "path:";
    private const string EnumPrefix = "enum:";
    private const string RangeSeparator = "..";
    private const char ChoiceSeparator = '|';
    private const string WordTail = "._:@-";
    private const string ChoiceChars = "._:@=,+-";

    /// <summary>Parses one template element, or returns null with the error code of what is wrong with it.</summary>
    public static TemplateElement? Parse(string? element, string os, out string? error)
    {
        error = null;
        if (element is null)
        {
            error = GrantErrors.Element;
            return null;
        }

        var open = element.IndexOf('{');
        var close = element.IndexOf('}');
        if (open < 0 && close < 0)
        {
            if (GrantTemplateRules.IsPlainLiteral(element, os))
            {
                return new TemplateElement(element, null);
            }

            error = GrantErrors.Element;
            return null;
        }

        var single = open >= 0 && close == element.Length - 1 && element.IndexOf('{', open + 1) < 0 && element.IndexOf('}') == close;
        var prefix = single ? element[..open] : string.Empty;
        var prefixOk = prefix.Length == 0 || (prefix.Length >= 2 && prefix[^1] == '=' && GrantTemplateRules.IsPlainLiteral(prefix, os));
        var placeholder = single && prefixOk ? ParseBody(element[(open + 1)..close]) : null;
        if (placeholder is null)
        {
            error = GrantErrors.Placeholder;
            return null;
        }

        return new TemplateElement(prefix, placeholder);
    }

    private static Placeholder? ParseBody(string body)
    {
        if (body == WordBody)
        {
            return new Placeholder(PlaceholderKind.Word, 0, 0, null, null);
        }

        if (body.StartsWith(IntPrefix, StringComparison.Ordinal))
        {
            var parts = body[IntPrefix.Length..].Split(RangeSeparator);
            if (parts.Length == 2 && TryParseDigits(parts[0], out var min) && TryParseDigits(parts[1], out var max) && min <= max)
            {
                return new Placeholder(PlaceholderKind.Int, min, max, null, null);
            }

            return null;
        }

        if (body.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            var root = body[PathPrefix.Length..];
            return root.Length == 0 ? null : new Placeholder(PlaceholderKind.Path, 0, 0, root, null);
        }

        if (body.StartsWith(EnumPrefix, StringComparison.Ordinal))
        {
            var choices = body[EnumPrefix.Length..].Split(ChoiceSeparator);
            return choices.All(IsChoice) ? new Placeholder(PlaceholderKind.Enum, 0, 0, null, choices) : null;
        }

        return null;
    }

    private static bool IsChoice(string choice) =>
        choice.Length is > 0 and <= MaxChoiceLength && choice.All(c => char.IsAsciiLetterOrDigit(c) || ChoiceChars.Contains(c));

    // ASCII digits only, no sign, no leading zero (but "0" itself), at most nine of them so the value fits an int.
    private static bool TryParseDigits(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > MaxDigits || (text.Length > 1 && text[0] == '0') || !text.All(char.IsAsciiDigit))
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Lexical check of one value against its placeholder; never touches the file system.</summary>
    public static bool ValueMatches(Placeholder placeholder, string value, string os)
    {
        switch (placeholder.Kind)
        {
            case PlaceholderKind.Int:
                return TryParseDigits(value, out var number) && number >= placeholder.Min && number <= placeholder.Max;
            case PlaceholderKind.Word:
                return IsWord(value);
            case PlaceholderKind.Path:
                return IsPathValue(value, placeholder.Root, os);
            case PlaceholderKind.Enum:
                return placeholder.Choices is not null && placeholder.Choices.Contains(value, StringComparer.Ordinal);
            default:
                return false;
        }
    }

    private static bool IsWord(string value)
    {
        if (value.Length is 0 or > MaxWordLength || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(c => char.IsAsciiLetterOrDigit(c) || WordTail.Contains(c));
    }

    private static bool IsPathValue(string value, string? root, string os)
    {
        if (root is null || value.Length == 0 || value[0] is '-' or '+')
        {
            return false;
        }

        // The root is checked as well, so a template that was never validated cannot widen what a value may be.
        if (GrantTemplateRules.PathSegments(root, os, allowSpace: false, allowTrailingSeparator: true) is null
            || GrantTemplateRules.PathSegments(value, os, allowSpace: false, allowTrailingSeparator: false) is null)
        {
            return false;
        }

        return GrantTemplateRules.IsUnder(value, root, os);
    }
}
