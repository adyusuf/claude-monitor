using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Projects;

/// <summary>Validation of what a person types into a sprint (ADR-0011 §5). Errors are i18n keys.</summary>
internal static class SprintRules
{
    public const int NameMax = 100;
    public const int GoalMax = 1000;
    public const int TeamMax = 50;
    public const decimal CapacityMax = 99_999_999m;

    public static IResult? Check(SprintInput i)
    {
        if (i.Name is { } n && n.Trim().Length > NameMax) return Http.Invalid("name", "too_long");
        if (i.Goal is { } g && g.Trim().Length > GoalMax) return Http.Invalid("goal", "too_long");
        if (i.StartDate is { } start && i.EndDate is { } end && end < start) return Http.Invalid("endDate", "before_start");
        if (i.Capacity is < 0 or > CapacityMax || (i.Capacity is { } c && decimal.Round(c, 2) != c)) return Http.Invalid("capacity", "out_of_range");
        if (i.TeamUserIds is { } team && (team.Count > TeamMax || team.Distinct().Count() != team.Count)) return Http.Invalid("teamUserIds", "invalid_list");
        return null;
    }

    /// <summary>The relational rules: the team must be able to work, the release must be live and belong to the project (and to the sprint's application).</summary>
    public static async Task<IResult?> CheckReferencesAsync(MonitorDb db, ManagedProject project, Guid? applicationId, SprintInput i, CancellationToken ct)
    {
        foreach (var user in i.TeamUserIds ?? [])
        {
            if (!await ProjectRules.IsWorkerAsync(db, user, project.WorkspaceId, ct)) return Http.Invalid("teamUserIds", "not_a_member");
        }

        if (i.ReleaseId is { } release)
        {
            var ok = await (from r in db.ApplicationReleases.AsNoTracking()
                            join a in db.Applications.AsNoTracking() on r.ApplicationId equals a.Id
                            where r.Id == release && r.Status != ReleaseStatuses.Cancelled && a.ProjectId == project.Id && (applicationId == null || a.Id == applicationId)
                            select r.Id).AnyAsync(ct);
            if (!ok) return Http.Invalid("releaseId", "invalid_release");
        }

        return null;
    }

    /// <summary>The numeric value of an estimate, or 0 for a T-shirt size or nothing.</summary>
    public static decimal Number(string? estimate) =>
        decimal.TryParse(estimate, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
}
