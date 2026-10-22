using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

public sealed record SpaceImportResult(string Key, string Outcome, int Created, int Updated);

public sealed record SprintImportResult(string SprintId, string Outcome, int Created, int Updated);

/// <summary>
/// The two imports a person asks for (ADR-0015 §4): chosen spaces become projects with their backlog, their epics and their people; chosen
/// sprints of an imported space become sprints with their issues. Reads with the connector's account. A space that cannot be read is reported
/// by a short code and does not stop the others.
/// </summary>
public sealed class ImportService(PodiumDb db, IWorkTracker tracker, IntegrationTokens tokens, ProjectMirror projects, IssueMirror issues,
    SprintMirror sprints, PeopleMirror people, WebhookManager webhooks, TimeProvider clock)
{
    public const string Ok = "ok";
    public const string NotFound = "space_not_found";
    public const string NeedsSignIn = "reauthorize";

    public Task<IReadOnlyList<SpaceImportResult>> ImportSpacesAsync(IntegrationConnection connection, IReadOnlyList<string> keys, Guid actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ConnectionGate.RunAsync<IReadOnlyList<SpaceImportResult>>(connection.Id, async () =>
        {
            if (await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return [.. keys.Select(k => new SpaceImportResult(k, NeedsSignIn, 0, 0))];
            var results = new List<SpaceImportResult>();
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                var run = await StartRunAsync(connection, ct);
                var counts = new MirrorCounts();
                string outcome;
                try
                {
                    outcome = await ImportSpaceAsync(connection, ctx, key, actor, counts, ct);
                }
                catch (TrackerException e)
                {
                    outcome = e.Code;
                }

                await FinishRunAsync(run, counts, outcome, ct);
                results.Add(new SpaceImportResult(key, outcome, counts.Created, counts.Updated));
            }

            await EnsureWebhooksAsync(connection, ct);
            await people.InviteAsync(connection, ct);
            return results;
        }, ct);
    }

    public Task<IReadOnlyList<SprintImportResult>> ImportSprintsAsync(IntegrationConnection connection, string spaceKey, IReadOnlyList<string> sprintIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ConnectionGate.RunAsync<IReadOnlyList<SprintImportResult>>(connection.Id, async () =>
        {
            if (await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return [.. sprintIds.Select(s => new SprintImportResult(s, NeedsSignIn, 0, 0))];
            var run = await StartRunAsync(connection, ct);
            var counts = new MirrorCounts();
            var results = new List<SprintImportResult>();
            try
            {
                results.AddRange(await ImportSprintsCoreAsync(connection, ctx, spaceKey, sprintIds, counts, ct));
            }
            catch (TrackerException e)
            {
                results.AddRange(sprintIds.Select(s => new SprintImportResult(s, e.Code, 0, 0)));
            }

            await FinishRunAsync(run, counts, results.Any(r => r.Outcome != Ok) ? SyncStatuses.Partial : Ok, ct);
            await people.InviteAsync(connection, ct);
            return results;
        }, ct);
    }

    private async Task<string> ImportSpaceAsync(IntegrationConnection connection, TrackerContext ctx, string key, Guid actor, MirrorCounts counts, CancellationToken ct)
    {
        var space = await tracker.GetContainerAsync(ctx, key, ct);
        if (space is null) return NotFound;
        var backlog = await tracker.ListBacklogAsync(ctx, key, ct);
        var epics = await MissingEpicsAsync(ctx, backlog, new HashSet<string>(), ct);
        var everyone = (await tracker.ListPeopleAsync(ctx, key, ct)).Concat(PeopleOf(backlog.Concat(epics))).Concat(space.Lead is null ? [] : [space.Lead]).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var project = await projects.UpsertAsync(connection, space, actor, counts, ct);
        var persons = await people.UpsertAsync(connection, everyone, counts, ct);
        var state = await MirrorState.LoadAsync(db, connection, project, ct);
        foreach (var item in EpicsFirst(epics.Concat(backlog))) issues.Upsert(state, item, persons, connection.SiteHost, counts);
        await AddLeadAsync(project, space.Lead, persons, ct);
        WriteAudit(connection, actor, "space", space.Key, counts);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Ok;
    }

    private async Task<List<SprintImportResult>> ImportSprintsCoreAsync(IntegrationConnection connection, TrackerContext ctx, string spaceKey, IReadOnlyList<string> sprintIds,
        MirrorCounts counts, CancellationToken ct)
    {
        var link = await db.IntegrationLinks.FirstOrDefaultAsync(l => l.ConnectionId == connection.Id && l.Kind == LinkKinds.Project && l.ExternalKey == spaceKey, ct);
        var project = link is null ? null : await db.Projects.FirstOrDefaultAsync(p => p.Id == link.LocalId, ct);
        if (project is null) return [.. sprintIds.Select(s => new SprintImportResult(s, NotFound, 0, 0))];
        var available = (await tracker.ListIterationsAsync(ctx, spaceKey, ct)).ToDictionary(s => s.Id);
        var chosen = sprintIds.Distinct(StringComparer.Ordinal).Where(available.ContainsKey).ToList();
        var items = new Dictionary<string, IReadOnlyList<ExternalItem>>();
        foreach (var id in chosen) items[id] = await tracker.ListIterationItemsAsync(ctx, id, ct);
        var all = items.Values.SelectMany(i => i).GroupBy(i => i.Id).Select(g => g.First()).ToList();
        var epics = await MissingEpicsAsync(ctx, all, await KnownKeysAsync(connection, project, ct), ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var persons = await people.UpsertAsync(connection, PeopleOf(all.Concat(epics)), counts, ct);
        var state = await MirrorState.LoadAsync(db, connection, project, ct);
        foreach (var id in chosen) sprints.Upsert(state, available[id], counts); // first, so that an issue finds the sprint it sits in
        foreach (var item in EpicsFirst(epics.Concat(all))) issues.Upsert(state, item, persons, connection.SiteHost, counts);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return [.. sprintIds.Select(s => new SprintImportResult(s, chosen.Contains(s) ? Ok : NotFound, 0, 0))];
    }

    /// <summary>The epics that the given issues hang under and that Podium does not have yet (and are not in the list itself).</summary>
    internal async Task<List<ExternalItem>> MissingEpicsAsync(TrackerContext ctx, IReadOnlyList<ExternalItem> items, IReadOnlySet<string> known, CancellationToken ct)
    {
        var present = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var wanted = items.Where(i => i.ParentKey is not null && "Epic".Equals(i.ParentTypeName, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.ParentKey!).Distinct(StringComparer.Ordinal).Where(k => !present.Contains(k) && !known.Contains(k)).ToList();
        var epics = new List<ExternalItem>();
        foreach (var key in wanted)
        {
            if (await tracker.GetItemAsync(ctx, key, ct) is { } epic) epics.Add(epic);
        }

        return epics;
    }

    /// <summary>Epics before the issues under them, so a child finds its parent in the same run; otherwise the order of the tool is kept.</summary>
    internal static List<ExternalItem> EpicsFirst(IEnumerable<ExternalItem> items) =>
        [.. items.OrderBy(i => JiraMapping.Type(i.TypeName, i.IsSubtask) == Data.WorkItemTypes.Epic ? 0 : 1)];

    internal static List<ExternalPerson> PeopleOf(IEnumerable<ExternalItem> items) =>
        [.. items.SelectMany(i => new[] { i.Assignee, i.Reporter }).Where(p => p is not null).Select(p => p!)];

    private async Task<HashSet<string>> KnownKeysAsync(IntegrationConnection connection, ManagedProject project, CancellationToken ct)
    {
        var local = await db.Issues.AsNoTracking().Where(i => i.ProjectId == project.Id && i.ExternalKey != null).Select(i => i.ExternalKey!).ToListAsync(ct);
        return [.. local];
    }

    private async Task AddLeadAsync(ManagedProject project, ExternalPerson? lead, Dictionary<string, IntegrationPerson> persons, CancellationToken ct)
    {
        if (lead is null || !persons.TryGetValue(lead.Id, out var person) || person.UserId is not { } user || user == project.OwnerUserId) return;
        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == user, ct)) return;
        if (await Access.MemberAsync(db, user, project.WorkspaceId, Roles.Member, ct) is null) return;
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = user, Role = ProjectRoles.Manager, AddedBy = project.OwnerUserId, AddedAt = clock.GetUtcNow() });
    }

    private async Task EnsureWebhooksAsync(IntegrationConnection connection, CancellationToken ct)
    {
        var keys = await db.IntegrationLinks.AsNoTracking().Where(l => l.ConnectionId == connection.Id && l.Kind == LinkKinds.Project && l.ExternalKey != null)
            .Select(l => l.ExternalKey!).ToListAsync(ct);
        var tracked = await db.IntegrationConnections.FirstAsync(c => c.Id == connection.Id, ct);
        await webhooks.EnsureAsync(tracked, keys, ct);
        await db.SaveChangesAsync(ct);
    }

    private void WriteAudit(IntegrationConnection connection, Guid actor, string what, string key, MirrorCounts counts) =>
        Audit.Add(db, null, clock, AuditActions.IntegrationImported, connection.WorkspaceId, actor, targetType: "integration", targetId: connection.Id,
            detail: new { what, key, counts.Created, counts.Updated });

    private async Task<IntegrationSyncRun> StartRunAsync(IntegrationConnection connection, CancellationToken ct)
    {
        var run = new IntegrationSyncRun { ConnectionId = connection.Id, Kind = SyncKinds.Import, StartedAt = clock.GetUtcNow() };
        db.IntegrationSyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task FinishRunAsync(IntegrationSyncRun run, MirrorCounts counts, string outcome, CancellationToken ct)
    {
        run.Created = counts.Created;
        run.Updated = counts.Updated;
        run.People = counts.People;
        run.Status = outcome == Ok ? SyncStatuses.Ok : SyncStatuses.Failed;
        run.Error = outcome == Ok ? null : outcome;
        run.EndedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }
}
