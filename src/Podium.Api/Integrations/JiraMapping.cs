using System.Globalization;
using Podium.Api.Data;

namespace Podium.Api.Integrations;

/// <summary>Jira's words to Podium's closed sets (ADR-0015 §4). Pure, so a test walks the whole table; an unknown word never fails an import.</summary>
public static class JiraMapping
{
    public const int TitleMax = 200;
    public const int TextMax = 20000;
    private static readonly string[] CancelledResolutions = ["won't do", "wont do", "won't fix", "cannot reproduce", "duplicate", "declined", "rejected"];

    public static string Type(string typeName, bool isSubtask)
    {
        if (isSubtask) return WorkItemTypes.Task;
        var name = typeName.Trim().ToLowerInvariant();
        return name switch
        {
            "epic" => WorkItemTypes.Epic,
            "story" or "user story" => WorkItemTypes.Story,
            "bug" or "defect" => WorkItemTypes.Bug,
            _ when name.Contains("spike", StringComparison.Ordinal) => WorkItemTypes.Spike,
            _ => WorkItemTypes.Task,
        };
    }

    /// <summary>"1" (highest) to "5" (lowest) on the one-to-five scale, or null for a priority this table does not know.</summary>
    public static string? Priority(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "highest" or "blocker" => "1",
        "high" or "critical" or "major" => "2",
        "medium" or "normal" => "3",
        "low" or "minor" => "4",
        "lowest" or "trivial" => "5",
        _ => null,
    };

    /// <summary>
    /// The status of an issue. <paramref name="category"/> is Jira's status category key (<c>new</c>, <c>indeterminate</c>, <c>done</c>);
    /// <paramref name="inSprint"/> tells a waiting issue apart: in a sprint it is ready, in the backlog it is not.
    /// </summary>
    public static string Status(string category, string statusName, string? resolution, bool inSprint)
    {
        switch (category.Trim().ToLowerInvariant())
        {
            case "done":
                return resolution is { } r && CancelledResolutions.Contains(r.Trim().ToLowerInvariant()) ? IssueStatuses.Cancelled : IssueStatuses.Done;
            case "indeterminate":
                return statusName.Contains("review", StringComparison.OrdinalIgnoreCase) ? IssueStatuses.InReview : IssueStatuses.InProgress;
            default:
                return inSprint ? IssueStatuses.Ready : IssueStatuses.Backlog;
        }
    }

    public static string Sprint(string state) => state.Trim().ToLowerInvariant() switch
    {
        "active" => SprintStatuses.Active,
        "closed" => SprintStatuses.Closed,
        _ => SprintStatuses.Planned,
    };

    /// <summary>A story-point value in the stored form (at most two decimals), or null.</summary>
    public static string? Estimate(double? points)
    {
        if (points is not { } p || double.IsNaN(p) || double.IsInfinity(p) || p < 0 || p > 9999.99) return null;
        return Math.Round((decimal)p, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Text from the tool, cleaned of control characters and cut to a length (untrusted input).</summary>
    public static string? Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= max ? clean : clean[..max];
    }
}
