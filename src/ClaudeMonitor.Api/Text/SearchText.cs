using System.Globalization;
using System.Text;

namespace ClaudeMonitor.Api.Text;

/// <summary>
/// The ONE normaliser for search and for e-mail comparison (global #13): case- and accent-insensitive, so
/// "sisman" matches "Şişman" and "istanbul" matches "İstanbul". Every *_search column is filled by it, and every
/// search term passes through it before comparison.
/// </summary>
public static class SearchText
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        // Turkish dotted/dotless i first: invariant lower-casing would leave "ı" and turn "İ" into "i̇".
        var mapped = value.Trim().Replace('ı', 'i').Replace('İ', 'i').Replace('I', 'i');
        var decomposed = mapped.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A LIKE pattern for "contains", with the user's own wildcards escaped.</summary>
    public static string ContainsPattern(string term) =>
        "%" + Normalize(term).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>E-mail addresses compare on the normalised form; the original spelling is kept for display.</summary>
    public static string Email(string email) => Normalize(email);
}
