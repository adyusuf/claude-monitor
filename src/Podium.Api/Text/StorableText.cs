using System.Globalization;

namespace Podium.Api.Text;

/// <summary>
/// What PostgreSQL refuses to store: a NUL (neither jsonb nor text takes U+0000) and half a surrogate pair (not valid UTF-8 or JSON text).
/// One such character in a captured event would fail the whole batch, so the event is judged before it is stored.
/// </summary>
public static class StorableText
{
    private const int HighFirst = 0xD800;
    private const int HighLast = 0xDBFF;
    private const int LowFirst = 0xDC00;
    private const int LowLast = 0xDFFF;

    /// <summary>
    /// Raw JSON text as it arrived. Valid JSON can carry the two only as "\u" escapes (a raw NUL is not allowed in a string and a raw half
    /// pair is not valid UTF-8), and the agent writes ASCII-escaped JSON, so the escapes are what is read here.
    /// </summary>
    public static bool Json(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\')
            {
                continue;
            }

            i++; // the escaped character: "\\" and "\"" are skipped whole, so an escaped backslash before "u0000" is not read as an escape
            if (i + 4 >= raw.Length || raw[i] != 'u')
            {
                continue;
            }

            var code = Hex(raw, i + 1);
            if (code == 0 || code is >= LowFirst and <= LowLast)
            {
                return false;
            }

            if (code is >= HighFirst and <= HighLast)
            {
                // a high half must be followed at once by an escaped low half
                if (i + 10 >= raw.Length || raw[i + 5] != '\\' || raw[i + 6] != 'u' || Hex(raw, i + 7) is < LowFirst or > LowLast)
                {
                    return false;
                }

                i += 10;
            }
            else
            {
                i += 4;
            }
        }

        return true;
    }

    /// <summary>A value that is already text (a field of the event): no NUL and no half of a surrogate pair. Null is storable.</summary>
    public static bool Plain(string? value)
    {
        if (value is null)
        {
            return true;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int Hex(string raw, int at) =>
        int.TryParse(raw.AsSpan(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) ? code : -1;
}
