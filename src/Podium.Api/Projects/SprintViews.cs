using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Projects;

/// <summary>Maps sprint rows to their wire shapes; the counts and sums are read from the sprint's issues each time, never stored.</summary>
internal static class SprintViews
{
    public static async Task<List<SprintSummary>> SummariesAsync(PodiumDb db, IReadOnlyList<Sprint> sprints, CancellationToken ct)
    {
        var ids = sprints.Select(s => s.Id).ToList();
        var issues = await db.Issues.AsNoTracking().Where(i => i.SprintId != null && ids.Contains(i.SprintId.Value) && i.ArchivedAt == null)
            .Select(i => new { Sprint = i.SprintId!.Value, i.Status, i.Estimate }).ToListAsync(ct);
        var releaseIds = sprints.Where(s => s.ReleaseId != null).Select(s => s.ReleaseId!.Value).Distinct().ToList();
        var releases = await db.ApplicationReleases.AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        return sprints.Select(s =>
        {
            var mine = issues.Where(i => i.Sprint == s.Id).ToList();
            return new SprintSummary(s.Id, s.ProjectId, s.ApplicationId, s.Number, s.Name, s.Goal, s.StartDate, s.EndDate, s.Capacity, s.Status, s.ReleaseId,
                s.ReleaseId is { } r ? releases.GetValueOrDefault(r) : null, mine.Count,
                mine.Where(i => i.Status != IssueStatuses.Cancelled).Sum(i => SprintRules.Number(i.Estimate)),
                mine.Where(i => i.Status == IssueStatuses.Done).Sum(i => SprintRules.Number(i.Estimate)), s.StartedAt, s.ClosedAt, s.CreatedAt, s.SourceConnectionId);
        }).ToList();
    }

    public static async Task<SprintView> ViewAsync(PodiumDb db, Sprint sprint, bool canEdit, CancellationToken ct)
    {
        var summary = (await SummariesAsync(db, [sprint], ct))[0];
        var team = await (from m in db.SprintMembers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.SprintId == sprint.Id
                          orderby u.DisplayName
                          select new SprintMemberView(u.Id, u.DisplayName)).ToListAsync(ct);
        var counts = IssueStatuses.All.ToDictionary(s => s, _ => 0);
        var grouped = await db.Issues.AsNoTracking().Where(i => i.SprintId == sprint.Id && i.ArchivedAt == null).GroupBy(i => i.Status)
            .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        foreach (var g in grouped) counts[g.Key] = g.N;
        return new SprintView(summary, team, counts, canEdit);
    }
}
