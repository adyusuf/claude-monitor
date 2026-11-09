namespace Podium.Contracts;

/// <summary>Characters that hide what a text says, refused wherever a person must see exactly what they approve or set (ADR-0019, ADR-0020).</summary>
public static class TextSafety
{
    /// <summary>Control characters other than a newline or a tab, bidirectional overrides and zero-width characters.</summary>
    public static bool IsHidden(char c) =>
        (char.IsControl(c) && c is not ('\n' or '\t')) || c is >= '‪' and <= '‮' || c is >= '⁦' and <= '⁩'
        || c is '​' or '‌' or '‍' or '⁠' or '﻿';

    /// <summary>True when the text holds a character <see cref="IsHidden"/> refuses.</summary>
    public static bool HasHidden(string? text) => text is not null && text.Any(IsHidden);
}
