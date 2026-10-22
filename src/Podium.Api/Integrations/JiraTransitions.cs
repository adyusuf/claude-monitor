using System.Text.Json;
using Podium.Api.Data;

namespace Podium.Api.Integrations;

/// <summary>Picks, among the steps a Jira issue's workflow offers, the one that fits a Podium status (ADR-0015 §7). Pure, so a test walks the table.</summary>
public static class JiraTransitions
{
    private static readonly string[] CancelWords = ["won't", "wont", "cancel", "reject", "declin", "invalid", "duplicate"];
    private static readonly string[] DoneWords = ["done", "complete", "resolve", "closed", "finish"];

    /// <summary>The id of the transition for the status, or null when the workflow has none. <paramref name="transitions"/> is the <c>transitions</c> array.</summary>
    public static string? Pick(JsonElement transitions, string podiumStatus)
    {
        if (transitions.ValueKind != JsonValueKind.Array) return null;
        var wanted = Category(podiumStatus);
        var options = transitions.EnumerateArray()
            .Select(t => (Id: JiraParse.Str(t, "id"), To: t.TryGetProperty("to", out var to) ? to : default))
            .Where(x => x.Id is not null && x.To.ValueKind == JsonValueKind.Object)
            .Select(x => new Option(x.Id!, (JiraParse.Str(x.To, "name") ?? "").ToLowerInvariant(),
                x.To.TryGetProperty("statusCategory", out var c) ? JiraParse.Str(c, "key") : null))
            .Where(x => x.Category == wanted)
            .ToList();
        if (options.Count == 0) return null;
        var hint = podiumStatus switch
        {
            IssueStatuses.InReview => new[] { "review" },
            IssueStatuses.Cancelled => CancelWords,
            IssueStatuses.Done => DoneWords,
            IssueStatuses.InProgress => new[] { "progress", "doing", "develop" },
            _ => Array.Empty<string>(),
        };
        var avoid = podiumStatus == IssueStatuses.InProgress ? new[] { "review" } : podiumStatus == IssueStatuses.Done ? CancelWords : [];
        var best = options.FirstOrDefault(o => hint.Any(h => o.Name.Contains(h, StringComparison.Ordinal)))
                   ?? options.FirstOrDefault(o => !avoid.Any(a => o.Name.Contains(a, StringComparison.Ordinal)));
        return (best ?? options[0]).Id;
    }

    private sealed record Option(string Id, string Name, string? Category);

    /// <summary>Jira's status category for a Podium status.</summary>
    public static string Category(string podiumStatus) => podiumStatus switch
    {
        IssueStatuses.InProgress or IssueStatuses.InReview => "indeterminate",
        IssueStatuses.Done or IssueStatuses.Cancelled => "done",
        _ => "new",
    };
}
