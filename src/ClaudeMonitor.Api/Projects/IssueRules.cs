using System.Globalization;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Projects;

/// <summary>Validation of what a person types into an issue, against the project's own settings (ADR-0011 §4). Errors are i18n keys.</summary>
internal static class IssueRules
{
    public const int TitleMax = 200;
    public const int TextMax = 20000;
    public const int StepsMax = 5000;
    public const int ResultMax = 2000;
    public const int CriteriaMax = 30;
    public const int CriterionMax = 500;
    public const decimal EstimateMax = 9999.99m;
    public static readonly IReadOnlyList<string> TShirtSizes = ["xs", "s", "m", "l", "xl"];

    /// <summary>The values a priority may take on the project's scale.</summary>
    public static IReadOnlyList<string> Priorities(string scale) => scale switch
    {
        PriorityScales.P0ToP3 => ["p0", "p1", "p2", "p3"],
        PriorityScales.MoSCoW => ["must", "should", "could", "wont"],
        PriorityScales.HighMediumLow => ["high", "medium", "low"],
        PriorityScales.OneToFive => ["1", "2", "3", "4", "5"],
        _ => [],
    };

    /// <summary>Checks everything that needs no database read; the first failure is the answer, null means valid.</summary>
    public static IResult? Check(IssueInput i, ManagedProject project)
    {
        if (!Http.IsName(i.Title, TitleMax)) return Http.Invalid("title", "invalid_name");
        if (i.Type is { } t && (!WorkItemTypes.All.Contains(t) || !project.ItemTypes.Contains(t))) return Http.Invalid("type", "invalid_value");
        if (i.Priority is { Length: > 0 } p && !Priorities(project.PriorityScale).Contains(p)) return Http.Invalid("priority", "invalid_value");
        if (i.Estimate is { Length: > 0 } e && Estimate(e, project.EstimationUnit) is null) return Http.Invalid("estimate", "invalid_value");
        if (i.Status is { } s && !IssueStatuses.All.Contains(s)) return Http.Invalid("status", "invalid_value");
        if (TooLong(i.Description, TextMax)) return Http.Invalid("description", "too_long");
        if (TooLong(i.TechnicalDesign, TextMax)) return Http.Invalid("technicalDesign", "too_long");
        if (i.AcceptanceCriteria is { } c && (c.Count > CriteriaMax || c.Any(x => !Part(x.Given) || !Part(x.When) || !Part(x.Then)))) return Http.Invalid("acceptanceCriteria", "invalid_list");
        if (TooLong(i.BugEnvironment, ProjectRules.ShortMax)) return Http.Invalid("bugEnvironment", "too_long");
        if (TooLong(i.ReproSteps, StepsMax)) return Http.Invalid("reproSteps", "too_long");
        if (TooLong(i.ExpectedResult, ResultMax)) return Http.Invalid("expectedResult", "too_long");
        if (TooLong(i.ActualResult, ResultMax)) return Http.Invalid("actualResult", "too_long");
        if (i.Severity is { Length: > 0 } v && !BugSeverities.All.Contains(v)) return Http.Invalid("severity", "invalid_value");
        return null;
    }

    /// <summary>The bug fields belong to a bug and to no other type.</summary>
    public static IResult? CheckBugFields(IssueInput i, string type)
    {
        if (type == WorkItemTypes.Bug) return null;
        foreach (var (field, value) in new[] { ("bugEnvironment", i.BugEnvironment), ("reproSteps", i.ReproSteps), ("expectedResult", i.ExpectedResult), ("actualResult", i.ActualResult), ("severity", i.Severity) })
        {
            if (!string.IsNullOrWhiteSpace(value)) return Http.Invalid(field, "bug_only");
        }

        return null;
    }

    /// <summary>The estimate in its stored form for the unit, or null when it does not fit: a number with at most two decimals, or a T-shirt size.</summary>
    public static string? Estimate(string value, string unit)
    {
        var text = value.Trim();
        if (unit == EstimationUnits.TShirt)
        {
            var size = text.ToLowerInvariant();
            return TShirtSizes.Contains(size) ? size : null;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0 || number > EstimateMax
            || decimal.Round(number, 2) != number)
        {
            return null;
        }

        return number.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>The relational rules: every named person, issue, application, component and release must exist in this project and be usable.</summary>
    public static async Task<IResult?> CheckReferencesAsync(MonitorDb db, ManagedProject project, Issue issue, string type, CancellationToken ct)
    {
        if (issue.AssigneeUserId is { } who && !await ProjectRules.IsWorkerAsync(db, who, project.WorkspaceId, ct)) return Http.Invalid("assigneeUserId", "not_a_member");
        if (issue.ParentId is { } parent)
        {
            var ok = type != WorkItemTypes.Epic && parent != issue.Id
                && await db.Issues.AnyAsync(x => x.Id == parent && x.ProjectId == project.Id && x.Type == WorkItemTypes.Epic && x.ArchivedAt == null, ct);
            if (!ok) return Http.Invalid("parentId", "invalid_parent");
        }

        if (issue.ApplicationId is { } app
            && !await db.Applications.AnyAsync(a => a.Id == app && a.ProjectId == project.Id && a.Status == ManagedStatuses.Active, ct))
        {
            return Http.Invalid("applicationId", "invalid_application");
        }

        if (issue.ComponentId is { } component
            && !await db.ApplicationComponents.AnyAsync(c => c.Id == component && c.ApplicationId == issue.ApplicationId && c.Status == ManagedStatuses.Active, ct))
        {
            return Http.Invalid("componentId", "invalid_component");
        }

        if (issue.ReleaseId is { } release
            && !await db.ApplicationReleases.AnyAsync(r => r.Id == release && r.ApplicationId == issue.ApplicationId && r.Status != ReleaseStatuses.Cancelled, ct))
        {
            return Http.Invalid("releaseId", "invalid_release");
        }

        return null;
    }

    public static string? Clean(string? value) => ProjectRules.Clean(value);

    private static bool TooLong(string? value, int max) => value is not null && value.Trim().Length > max;

    private static bool Part(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= CriterionMax;
}
