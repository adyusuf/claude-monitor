using System.Text;
using System.Text.Json;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>
/// Jira's rich text (Atlassian Document Format) to plain text and back. Reading keeps paragraphs, list items and mentions' text and drops
/// everything else (media, macros); the result is shown as text only. Writing makes one paragraph per line.
/// </summary>
public static class JiraAdf
{
    public const int DepthMax = 20;

    public static string? ToText(JsonElement doc, int max)
    {
        if (doc.ValueKind == JsonValueKind.String) return Cut(doc.GetString(), max);
        if (doc.ValueKind != JsonValueKind.Object) return null;
        var text = new StringBuilder();
        Walk(doc, text, 0, max);
        return Cut(text.ToString(), max);
    }

    private static void Walk(JsonElement node, StringBuilder text, int depth, int max)
    {
        if (depth > DepthMax || text.Length > max || node.ValueKind != JsonValueKind.Object) return;
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "text":
                if (node.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String) text.Append(value.GetString());
                return;
            case "hardBreak":
                text.Append('\n');
                return;
            case "mention":
                if (node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("text", out var mention) && mention.ValueKind == JsonValueKind.String)
                {
                    text.Append(mention.GetString());
                }

                return;
            case "listItem":
                text.Append("- ");
                break;
            default:
                break;
        }

        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray()) Walk(child, text, depth + 1, max);
        }

        if (type is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote" or "tableRow") text.Append('\n');
    }

    /// <summary>One ADF paragraph per line; an empty text is an empty document.</summary>
    public static object FromText(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var content = lines.Select(line => line.Length == 0
            ? (object)new { type = "paragraph" }
            : new { type = "paragraph", content = new[] { new { type = "text", text = line } } }).ToArray();
        return new { type = "doc", version = 1, content };
    }

    private static string? Cut(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= max ? clean : clean[..max];
    }
}
