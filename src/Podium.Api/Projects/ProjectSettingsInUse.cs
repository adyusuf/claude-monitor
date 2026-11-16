using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Projects;

/// <summary>
/// Refuses a project settings change that would strand issues (F-102): a type, a priority or an estimate that the project would no longer offer.
/// Archived issues count too, since a restore brings them back. The answer is 409 with a code that names the setting and the number of issues.
/// </summary>
internal static class ProjectSettingsInUse
{
    public const string ItemType = "item_type_in_use";
    public const string PriorityScale = "priority_scale_in_use";
    public const string EstimationUnit = "estimation_unit_in_use";

    /// <summary>The refusal for the first setting of <paramref name="input"/> that issues of the project still depend on, or null when the change strands none.</summary>
    public static async Task<IResult?> CheckAsync(PodiumDb db, ManagedProject project, ProjectInput input, CancellationToken ct)
    {
        var issues = db.Issues.AsNoTracking().Where(i => i.ProjectId == project.Id);
        if (input.ItemTypes is { } types)
        {
            var removed = project.ItemTypes.Except(types).ToList();
            if (removed.Count > 0 && await issues.CountAsync(i => removed.Contains(i.Type), ct) is var n and > 0) return InUse(ItemType, n);
        }

        if (input.PriorityScale is { } scale && scale != project.PriorityScale)
        {
            var offered = IssueRules.Priorities(scale);
            if (await issues.CountAsync(i => i.Priority != null && i.Priority != "" && !offered.Contains(i.Priority), ct) is var n and > 0) return InUse(PriorityScale, n);
        }

        if (input.EstimationUnit is { } unit && unit != project.EstimationUnit)
        {
            var held = await issues.Where(i => i.Estimate != null).Select(i => i.Estimate!).Distinct().ToListAsync(ct);
            var misfit = held.Where(e => IssueRules.Estimate(e, unit) is null).ToList();
            if (misfit.Count > 0 && await issues.CountAsync(i => misfit.Contains(i.Estimate!), ct) is var n and > 0) return InUse(EstimationUnit, n);
        }

        return null;
    }

    private static IResult InUse(string code, int count) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: code, extensions: new Dictionary<string, object?> { ["count"] = count });
}
