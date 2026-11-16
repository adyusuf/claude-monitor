using System.Text.Json;

namespace Podium.Contracts;

/// <summary>One question of the harness's question tool as it offered it: its options' labels, and whether more than one may be chosen.</summary>
public sealed record OfferedQuestion(IReadOnlyList<string> Labels, bool MultiSelect);

/// <summary>
/// How an answer to the question tool is read against what the question offered (Q-2, F-335), one definition for the API (which refuses an option
/// that was not offered) and the agent (which hands chosen options over as they are and wraps typed text as a message from the web). An answer is
/// the chosen labels, for a multiple choice joined by <see cref="Separator"/>, optionally followed by typed text (the "Other" answer).
/// </summary>
public static class QuestionAnswers
{
    public const string Separator = ", ";

    /// <summary>The questions the tool input carries, by their text; an entry without options (an older shape) has no labels.</summary>
    public static Dictionary<string, OfferedQuestion> Offered(JsonElement toolInput)
    {
        var offered = new Dictionary<string, OfferedQuestion>(StringComparer.Ordinal);
        if (toolInput.ValueKind != JsonValueKind.Object || !toolInput.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array) return offered;
        foreach (var q in list.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object || !q.TryGetProperty("question", out var text) || text.ValueKind != JsonValueKind.String) continue;
            var labels = new List<string>();
            if (q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in options.EnumerateArray())
                {
                    if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String) labels.Add(label.GetString()!);
                }
            }

            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            offered[text.GetString()!] = new OfferedQuestion(labels, multi);
        }

        return offered;
    }

    /// <summary>
    /// How many characters at the start of the answer are offered labels: one label for a single choice, distinct ones joined by the separator for a
    /// multiple choice, the longest label first where several fit. The whole length means the answer is only chosen options; 0, none at all.
    /// </summary>
    public static int ChosenLength(string answer, OfferedQuestion question)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(question);
        var labels = question.Labels.Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(l => l.Length).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var end = 0;
        var at = 0;
        while (at < answer.Length && (used.Count == 0 || question.MultiSelect))
        {
            var label = labels.FirstOrDefault(l => !used.Contains(l) && string.CompareOrdinal(answer, at, l, 0, l.Length) == 0
                && (at + l.Length == answer.Length || string.CompareOrdinal(answer, at + l.Length, Separator, 0, Separator.Length) == 0));
            if (label is null) break;
            used.Add(label);
            end = at + label.Length;
            at = end + Separator.Length;
        }

        return end;
    }
}
