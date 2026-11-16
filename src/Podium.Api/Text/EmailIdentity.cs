namespace Podium.Api.Text;

/// <summary>
/// The identity key of an e-mail address (F-180): trimmed and lower-cased, nothing else. Unlike <see cref="SearchText"/> it strips no
/// accents and folds no dotless i, so two different mailboxes ("josé@" and "jose@") never become one account; folding stays for search.
/// Lower-casing is the Unicode simple mapping, the one the migration <c>EmailKey</c> used in SQL (<c>lower(btrim(email) COLLATE pg_c_utf8)</c>):
/// .NET's invariant lower-casing is that mapping except for the capital dotted I (U+0130), which it leaves alone and Unicode maps to "i".
/// </summary>
public static class EmailIdentity
{
    private const char CapitalDottedI = 'İ';

    /// <summary>The key, or null for no address.</summary>
    public static string? Key(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant().Replace(CapitalDottedI, 'i');
}
