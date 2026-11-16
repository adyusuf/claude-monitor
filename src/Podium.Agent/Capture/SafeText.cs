using System.Text.Json;
using System.Text.Json.Nodes;
using Podium.Agent.Daemon;

namespace Podium.Agent.Capture;

/// <summary>
/// Text that survives the API's database: no NUL and no half of a surrogate pair (neither jsonb nor text stores them, and one such
/// character fails the whole batch it travels in), and cuts that never leave half of a character behind.
/// </summary>
public static class SafeText
{
    /// <summary>What a character the database cannot store becomes: visible, so the event still shows something was there.</summary>
    public const char Replacement = '�';

    /// <summary>The text cut to at most <paramref name="max"/> characters, one fewer when the cut would fall between the two halves of a pair (an emoji).</summary>
    public static string Cut(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max) return text;
        var end = Math.Max(max, 0);
        if (end is > 0 && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        return text[..end];
    }

    /// <summary>The text with every NUL and every lone half of a surrogate pair replaced; the same instance when there is none.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        char[]? fixedText = null;
        for (var i = 0; i < text.Length; i++)
        {
            var bad = text[i] == '\0';
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                bad = true;
            }

            if (!bad) continue;
            fixedText ??= text.ToCharArray();
            fixedText[i] = Replacement;
        }

        return fixedText is null ? text : new string(fixedText);
    }

    /// <summary>The node with every string value and every property name cleaned (see <see cref="Clean(string)"/>); a copy is cleaned, never the argument's children in place.</summary>
    public static JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var clean = new JsonObject();
                foreach (var (key, value) in o) clean[Clean(key)] = Clean(value?.DeepClone());
                return clean;
            case JsonArray a:
                var list = new JsonArray();
                foreach (var item in a) list.Add(Clean(item?.DeepClone()));
                return list;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(Clean(StringOf(v)));
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// A string value's text. One read from JSON text that holds a lone surrogate escape (JavaScript writes those) cannot be turned into a string
    /// by the reader, so its escapes are undone here and the half stays a lone character for <see cref="Clean(string)"/> to replace.
    /// </summary>
    private static string StringOf(JsonValue value)
    {
        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            // Not even ToJsonString can write it: only the element's raw text (still escaped) can be read.
            var quoted = value.GetValue<JsonElement>().GetRawText();
            return TranscriptTailer.UnescapeJson(quoted[1..^1]);
        }
    }
}
