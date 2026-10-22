using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Projects;

/// <summary>Maps issue rows to their wire shapes; the jsonb column holds the same list as the wire.</summary>
internal static class IssueViews
{
    public static IssueSummary Summary(Issue i, string? assigneeName) =>
        new(i.Id, i.ProjectId, i.ApplicationId, i.Number, i.Title, i.Type, i.Priority, i.Estimate, i.Status, i.AssigneeUserId, assigneeName,
            i.ParentId, i.ComponentId, i.ReleaseId, i.Position, i.ArchivedAt is not null, i.CreatedAt, i.UpdatedAt, i.SprintId, i.ExternalKey, i.ExternalUrl);

    public static async Task<Dictionary<Guid, string>> NamesAsync(PodiumDb db, IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>The name to show for each issue's assignee: the member's, or, for a mirrored issue assigned to someone who is no member, the tool's person.</summary>
    public static async Task<Func<Issue, string?>> AssigneeNamesAsync(PodiumDb db, IReadOnlyCollection<Issue> issues, CancellationToken ct)
    {
        var users = await NamesAsync(db, issues.Select(i => i.AssigneeUserId), ct);
        var personIds = issues.Where(i => i.AssigneeUserId is null && i.AssigneePersonId is not null).Select(i => i.AssigneePersonId!.Value).Distinct().ToList();
        var persons = personIds.Count == 0 ? [] : await db.IntegrationPeople.AsNoTracking().Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        return i => i.AssigneeUserId is { } u ? users.GetValueOrDefault(u) : i.AssigneePersonId is { } p ? persons.GetValueOrDefault(p) : null;
    }

    public static async Task<IssueView> ViewAsync(PodiumDb db, Issue i, bool canEdit, CancellationToken ct)
    {
        var assignee = await AssigneeNamesAsync(db, [i], ct);
        var parentTitle = i.ParentId is { } parent
            ? await db.Issues.AsNoTracking().Where(x => x.Id == parent).Select(x => x.Title).FirstOrDefaultAsync(ct)
            : null;
        var blocks = await (from l in db.IssueLinks.AsNoTracking()
                            join o in db.Issues.AsNoTracking() on l.BlockedIssueId equals o.Id
                            where l.BlockerIssueId == i.Id
                            orderby o.Number
                            select new IssueLinkView(l.Id, o.Id, o.Number, o.Title, o.Status)).ToListAsync(ct);
        var blockedBy = await (from l in db.IssueLinks.AsNoTracking()
                               join o in db.Issues.AsNoTracking() on l.BlockerIssueId equals o.Id
                               where l.BlockedIssueId == i.Id
                               orderby o.Number
                               select new IssueLinkView(l.Id, o.Id, o.Number, o.Title, o.Status)).ToListAsync(ct);
        return new IssueView(Summary(i, assignee(i)), i.Description,
            ProjectJson.Unpack<CriterionEntry>(i.AcceptanceCriteria), i.TechnicalDesign, i.BugEnvironment, i.ReproSteps, i.ExpectedResult,
            i.ActualResult, i.Severity, parentTitle, blocks, blockedBy, canEdit);
    }

    public static TestCaseView View(IssueTestCase t, string? resultByName) =>
        new(t.Id, t.IssueId, t.Title, t.Steps, t.Expected, t.Result, t.ResultNote, t.ResultBy, resultByName, t.ResultAt, t.CreatedAt);

    public static CodeLinkView View(IssueCodeLink c) => new(c.Id, c.IssueId, c.Kind, c.Url, c.Title, c.Source, c.CreatedAt);
}
