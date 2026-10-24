using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>
/// Keeps imported rows current (ADR-0015 §6): one issue or sprint read again after a webhook, and the reconciliation pass that catches what no
/// webhook delivered. Only spaces and sprints that were imported are touched; a sprint the person has not chosen stays out (decision 5).
/// </summary>
public sealed class SyncService(PodiumDb db, ApiConfig config, IWorkTracker tracker, IntegrationTokens tokens, IssueMirror issues, SprintMirror sprints,
    PeopleMirror people, ProjectMirror projects, WebhookManager webhooks, ImportService import, TimeProvider clock, ILogger<SyncService> log)
{
    public async Task SyncIssueAsync(IntegrationConnection connection, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ConnectionGate.RunAsync(connection.Id, async () =>
        {
            if (await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return 0;
            var item = await tracker.GetItemAsync(ctx, key, ct);
            var space = item?.ContainerKey ?? key[..Math.Max(key.LastIndexOf('-'), 0)];
            var project = await ProjectOfAsync(connection, space, ct);
            if (project is null) return 0;
            var counts = new MirrorCounts();
            var epics = item is null ? [] : await import.MissingEpicsAsync(ctx, [item], await KnownKeysAsync(project, ct), ct);
            var persons = await people.UpsertAsync(connection, ImportService.PeopleOf(item is null ? [] : [item, .. epics]), counts, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await MirrorState.LoadAsync(db, connection, project, ct);
            foreach (var next in epics) issues.Upsert(state, next, persons, connection.SiteHost, counts);
            if (item is not null) issues.Upsert(state, item, persons, connection.SiteHost, counts);
            else counts.Archived += ArchiveByKey(state, key, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return 1;
        }, ct);
    }

    public async Task SyncSprintAsync(IntegrationConnection connection, string sprintId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ConnectionGate.RunAsync(connection.Id, async () =>
        {
            var link = await db.IntegrationLinks.FirstOrDefaultAsync(l => l.ConnectionId == connection.Id && l.Kind == LinkKinds.Sprint && l.ExternalId == sprintId, ct);
            if (link is null || await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return 0; // not imported: nothing to keep
            var sprint = await db.Sprints.AsNoTracking().FirstAsync(s => s.Id == link.LocalId, ct);
            var project = await db.Projects.FirstAsync(p => p.Id == sprint.ProjectId, ct);
            var it = await tracker.GetIterationAsync(ctx, sprintId, ct);
            var counts = new MirrorCounts();
            var items = it is null ? [] : await tracker.ListIterationItemsAsync(ctx, sprintId, ct);
            var persons = await people.UpsertAsync(connection, ImportService.PeopleOf(items), counts, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await MirrorState.LoadAsync(db, connection, project, ct);
            if (it is null) sprints.Archive(state, state.SprintLinks[sprintId]);
            else
            {
                sprints.Upsert(state, it, counts);
                foreach (var item in ImportService.EpicsFirst(items)) issues.Upsert(state, item, persons, connection.SiteHost, counts);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return 1;
        }, ct);
    }

    /// <summary>The error a run is closed with when the pass failed for a reason that is not the tracker's (see PassAsync).</summary>
    public const string InternalError = "internal_error";

    /// <summary>One reconciliation pass over an active connection. Returns the run (also stored) so a caller can show it.</summary>
    public Task<IntegrationSyncRun> PassAsync(IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ConnectionGate.RunAsync(connection.Id, async () =>
        {
            var started = clock.GetUtcNow();
            var run = new IntegrationSyncRun { ConnectionId = connection.Id, Kind = SyncKinds.Pass, StartedAt = started };
            db.IntegrationSyncRuns.Add(run);
            await db.SaveChangesAsync(ct);
            var counts = new MirrorCounts();
            var tracked = await db.IntegrationConnections.FirstAsync(c => c.Id == connection.Id, ct);
            try
            {
                if (await tokens.ConnectorContextAsync(tracked, ct) is not { } ctx) throw new TrackerException(TrackerException.Codes.Unauthorized);
                await ReconcileAsync(tracked, ctx, counts, ct);
                tracked.LastPassAt = started;
                tracked.LastError = null;
                run.Status = SyncStatuses.Ok;
            }
            catch (TrackerException e)
            {
                tracked.LastError = e.Code;
                run.Status = SyncStatuses.Failed;
                run.Error = e.Code;
                log.LogWarning("Integration pass {Connection} failed: {Code}", connection.Id, e.Code);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not the tracker's doing (bad data, a bug, a database that went away). The run row was saved as "running" above, so it is closed
                // here, on a clean tracker (the failed save may have left the context in an unusable state), and the failure goes on to the caller,
                // which logs it and carries on with the next connection.
                log.LogWarning("Integration pass {Connection} failed unexpectedly: {Type}", connection.Id, e.GetType().Name);
                db.ChangeTracker.Clear();
                var ended = clock.GetUtcNow();
                await db.IntegrationSyncRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, SyncStatuses.Failed).SetProperty(r => r.Error, InternalError).SetProperty(r => r.EndedAt, ended), CancellationToken.None);
                throw;
            }

            run.Created = counts.Created;
            run.Updated = counts.Updated;
            run.Archived = counts.Archived;
            run.People = counts.People;
            run.EndedAt = clock.GetUtcNow();
            tracked.UpdatedAt = run.EndedAt.Value;
            await db.SaveChangesAsync(ct);
            return run;
        }, ct);
    }

    private async Task ReconcileAsync(IntegrationConnection connection, TrackerContext ctx, MirrorCounts counts, CancellationToken ct)
    {
        var links = await db.IntegrationLinks.AsNoTracking().Where(l => l.ConnectionId == connection.Id && l.Kind == LinkKinds.Project).ToListAsync(ct);
        var spaces = new List<(IntegrationLink Link, ExternalContainer Space)>();
        foreach (var link in links.Where(l => l.ExternalKey is not null))
        {
            var space = await tracker.GetContainerAsync(ctx, link.ExternalKey!, ct);
            if (space is null) await ArchiveSpaceAsync(link, counts, ct);
            else spaces.Add((link, space));
        }

        var since = (connection.LastPassAt ?? connection.CreatedAt) - config.IntegrationPassOverlap;
        foreach (var (link, space) in spaces)
        {
            var project = await db.Projects.FirstAsync(p => p.Id == link.LocalId, ct);
            if (project.Status != ManagedStatuses.Active) continue;
            var changed = await tracker.ListChangedAsync(ctx, [space.Key], since, ct);
            var alive = await tracker.ListItemIdsAsync(ctx, space.Key, ct);
            var iterations = await tracker.ListIterationsAsync(ctx, space.Key, ct);
            var epics = await import.MissingEpicsAsync(ctx, changed, await KnownKeysAsync(project, ct), ct);
            var everyone = (await tracker.ListPeopleAsync(ctx, space.Key, ct)).Concat(ImportService.PeopleOf(changed.Concat(epics))).Concat(space.Lead is null ? [] : [space.Lead]);
            var persons = await people.UpsertAsync(connection, everyone, counts, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await MirrorState.LoadAsync(db, connection, project, ct);
            await projects.UpsertAsync(connection, space, project.OwnerUserId, new MirrorCounts(), ct);
            foreach (var it in iterations.Where(i => state.SprintLinks.ContainsKey(i.Id))) sprints.Upsert(state, it, counts);
            var gone = state.SprintLinks.Where(l => iterations.All(i => i.Id != l.Key)).Select(l => l.Value).ToList();
            foreach (var g in gone) sprints.Archive(state, g);
            foreach (var item in ImportService.EpicsFirst(epics.Concat(changed))) issues.Upsert(state, item, persons, connection.SiteHost, counts);
            counts.Archived += issues.ArchiveMissing(state, alive.ToHashSet(StringComparer.Ordinal));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var keys = spaces.Select(s => s.Space.Key).ToList();
        await webhooks.ExtendIfDueAsync(connection, keys, ct);
        if (connection.WebhookIds is null && keys.Count > 0) await webhooks.EnsureAsync(connection, keys, ct);
        await people.InviteAsync(connection, ct);
    }

    private async Task ArchiveSpaceAsync(IntegrationLink link, MirrorCounts counts, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == link.LocalId, ct);
        if (project is null || project.Status == ManagedStatuses.Archived) return;
        project.Status = ManagedStatuses.Archived;
        project.UpdatedAt = clock.GetUtcNow();
        counts.Archived++;
        await db.SaveChangesAsync(ct);
    }

    private static int ArchiveByKey(MirrorState state, string key, DateTimeOffset now)
    {
        var issue = state.IssueOfKey(key);
        if (issue is null || issue.ArchivedAt is not null) return 0;
        issue.ArchivedAt = now;
        return 1;
    }

    private async Task<ManagedProject?> ProjectOfAsync(IntegrationConnection connection, string spaceKey, CancellationToken ct)
    {
        var link = await db.IntegrationLinks.AsNoTracking().FirstOrDefaultAsync(l => l.ConnectionId == connection.Id && l.Kind == LinkKinds.Project && l.ExternalKey == spaceKey, ct);
        return link is null ? null : await db.Projects.FirstOrDefaultAsync(p => p.Id == link.LocalId && p.Status == ManagedStatuses.Active, ct);
    }

    private async Task<HashSet<string>> KnownKeysAsync(ManagedProject project, CancellationToken ct) =>
        [.. await db.Issues.AsNoTracking().Where(i => i.ProjectId == project.Id && i.ExternalKey != null).Select(i => i.ExternalKey!).ToListAsync(ct)];
}
