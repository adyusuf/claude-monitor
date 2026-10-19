using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Projects;

/// <summary>Validation of what a person types into a project or an application. Errors are i18n keys (global #7: the API checks too).</summary>
internal static class ProjectRules
{
    public const int NameMax = 100;
    public const int TextMax = 2000;
    public const int ShortMax = 200;
    public const int UrlMax = 500;
    public const int ListMax = 30;
    public const int LineMax = 300;
    public const int StakeholdersMax = 50;
    public const decimal BudgetMax = 9_999_999_999_999m;

    /// <summary>Checks a project definition; the first failure is the answer, null means it is valid.</summary>
    public static IResult? Check(ProjectInput i)
    {
        if (!Http.IsName(i.Name, NameMax)) return Http.Invalid("name", "invalid_name");
        if (TooLong(i.Purpose, TextMax)) return Http.Invalid("purpose", "too_long");
        if (TooLong(i.Sponsor, ShortMax)) return Http.Invalid("sponsor", "too_long");
        if (i.Stakeholders is { } s && (s.Count > StakeholdersMax || s.Any(x => !Http.IsName(x.Name, NameMax) || TooLong(x.Role, NameMax))))
        {
            return Http.Invalid("stakeholders", "invalid_list");
        }

        if ((i.BudgetAmount is null) != string.IsNullOrWhiteSpace(i.BudgetCurrency)) return Http.Invalid("budget", "amount_and_currency");
        if (i.BudgetAmount is < 0 or > BudgetMax) return Http.Invalid("budgetAmount", "out_of_range");
        if (i.BudgetCurrency is { } c && !(c.Length == 3 && c.All(ch => ch is >= 'A' and <= 'Z'))) return Http.Invalid("budgetCurrency", "invalid_currency");
        if (i.StartDate is { } start && i.EndDate is { } end && end < start) return Http.Invalid("endDate", "before_start");
        if (i.ItemTypes is { } t && (t.Count == 0 || t.Distinct().Count() != t.Count || t.Any(x => !WorkItemTypes.All.Contains(x)))) return Http.Invalid("itemTypes", "invalid_list");
        if (i.PriorityScale is { } p && !PriorityScales.All.Contains(p)) return Http.Invalid("priorityScale", "invalid_value");
        if (i.EstimationUnit is { } u && !EstimationUnits.All.Contains(u)) return Http.Invalid("estimationUnit", "invalid_value");
        if (i.SprintLengthDays is < 1 or > 42) return Http.Invalid("sprintLengthDays", "out_of_range");
        if (!Lines(i.DefinitionOfReady)) return Http.Invalid("definitionOfReady", "invalid_list");
        if (!Lines(i.DefinitionOfDone)) return Http.Invalid("definitionOfDone", "invalid_list");
        return null;
    }

    public static IResult? Check(ApplicationInput i)
    {
        if (!Http.IsName(i.Name, NameMax)) return Http.Invalid("name", "invalid_name");
        if (TooLong(i.Description, TextMax)) return Http.Invalid("description", "too_long");
        if (i.Kind is { } k && !ApplicationKinds.All.Contains(k)) return Http.Invalid("kind", "invalid_value");
        if (!string.IsNullOrWhiteSpace(i.RepoUrl) && !RepoLink(i.RepoUrl.Trim())) return Http.Invalid("repoUrl", "invalid_url");
        if (i.Environments is { } e && (e.Count > EnvironmentNames.All.Count || e.Any(x => x.Name is null || !EnvironmentNames.All.Contains(x.Name)
            || (!string.IsNullOrWhiteSpace(x.Url) && !WebLink(x.Url.Trim())))
            || e.Select(x => x.Name).Distinct().Count() != e.Count))
        {
            return Http.Invalid("environments", "invalid_list");
        }

        if (i.TechStack is { } s && (s.Count > ListMax || s.Any(x => !Http.IsName(x, 60)) || s.Select(Normal).Distinct().Count() != s.Count))
        {
            return Http.Invalid("techStack", "invalid_list");
        }

        if (i.VersioningScheme is { } v && !VersioningSchemes.All.Contains(v)) return Http.Invalid("versioningScheme", "invalid_value");
        if (i.NonFunctionalTargets is { } n && (n.Count > 20 || n.Any(x => x.Kind is null || !TargetKinds.All.Contains(x.Kind) || !Http.IsName(x.Target, LineMax))))
        {
            return Http.Invalid("nonFunctionalTargets", "invalid_list");
        }

        return null;
    }

    /// <summary>A person of the workspace with at least the member role, or false (the owner and the technical owner must be able to work).</summary>
    public static async Task<bool> IsWorkerAsync(MonitorDb db, Guid userId, Guid workspaceId, CancellationToken ct) =>
        await Security.Access.MemberAsync(db, userId, workspaceId, Roles.Member, ct) is not null;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Normal(string value) => Text.SearchText.Normalize(value);

    public static List<string> CleanList(IEnumerable<string>? values) => (values ?? []).Select(v => v.Trim()).ToList();

    private static bool TooLong(string? value, int max) => value is not null && value.Trim().Length > max;

    private static bool Lines(List<string>? lines) =>
        lines is null || (lines.Count <= ListMax && lines.All(l => !string.IsNullOrWhiteSpace(l) && l.Trim().Length <= LineMax));

    /// <summary>An https, ssh or git address, or the scp form git@host:path; no password in it (a stored link is shown to the whole workspace).</summary>
    internal static bool RepoLink(string value)
    {
        if (value.Length > UrlMax || value.Any(char.IsControl)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._/~-]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50)))
        {
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" or "git" && !string.IsNullOrEmpty(uri.Host)
            && !uri.UserInfo.Contains(':', StringComparison.Ordinal);
    }

    /// <summary>An http or https address with no user information.</summary>
    internal static bool WebLink(string value) =>
        value.Length <= UrlMax && !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0;
}
