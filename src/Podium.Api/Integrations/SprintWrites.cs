using Podium.Api.Data;
using Podium.Api.Projects;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>
/// Writes a Podium change of a mirrored sprint, or of the issues in one, to the tool first (ADR-0015 §7). Same contract as <see cref="IssueWrites"/>:
/// null means go on with the local change, anything else is the refusal. A sprint and the issues in it must come from the same connection.
/// </summary>
public sealed class SprintWrites(PodiumDb db, IWorkWriter writer, MirrorGate gate, ILogger<SprintWrites> log)
{
    public async Task<IResult?> CreateAsync(Guid user, ManagedProject project, Sprint sprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sprint);
        if (project.SourceConnectionId is not { } connectionId) return null;
        var (open, refusal) = await gate.OpenAsync(connectionId, user, ct);
        if (open is null) return refusal;
        var space = await gate.ProjectKeyAsync(project.Id, ct);
        if (space is null) return MirrorGate.Problem(StatusCodes.Status409Conflict, "mirrored_detached");
        try
        {
            var made = await writer.CreateIterationAsync(open.Ctx, space, new IterationChange(sprint.Name, sprint.Goal, sprint.StartDate, sprint.EndDate), ct);
            if (made is null) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.NoBoard);
            sprint.SourceConnectionId = connectionId;
            db.IntegrationLinks.Add(new IntegrationLink { ConnectionId = connectionId, Kind = LinkKinds.Sprint, ExternalId = made.Id, LocalId = sprint.Id, SyncedAt = DateTimeOffset.UtcNow });
        }
        catch (TrackerException e)
        {
            return MirrorGate.Refusal(e);
        }

        return null;
    }

    /// <summary>
    /// Undoes <see cref="CreateAsync"/> when the local save that follows it failed: the sprint the tool made would otherwise stay there with no
    /// sprint here (a later sync pass would import it as a stranger). The tool's delete is tried once; when that fails too the loss is logged (the
    /// tool's id only, never the sprint's text) and the caller goes on with the first error.
    /// </summary>
    public async Task CompensateCreateAsync(Guid user, Sprint sprint)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        var link = db.IntegrationLinks.Local.FirstOrDefault(l => l.Kind == LinkKinds.Sprint && l.LocalId == sprint.Id);
        if (link is null) return;
        try
        {
            var (open, _) = await gate.OpenAsync(link.ConnectionId, user, CancellationToken.None);
            if (open is null) throw new TrackerException(TrackerException.Codes.Unauthorized);
            await writer.DeleteIterationAsync(open.Ctx, link.ExternalId, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning("A sprint made in the tool could not be deleted after the local save failed (tool sprint {ExternalId}, connection {Connection}): {Type}", link.ExternalId, link.ConnectionId, e.GetType().Name);
        }
    }

    public async Task<IResult?> UpdateAsync(Guid user, Sprint sprint, string? name, string? goal, DateOnly? start, DateOnly? end, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        if (sprint.SourceConnectionId is null) return null;
        // A null date means "unchanged" toward the tool, so a cleared date would never reach it: a mirrored sprint keeps the dates it has.
        if ((start is null && sprint.StartDate is not null) || (end is null && sprint.EndDate is not null)) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.DatesKept);
        var change = new IterationChange(name == sprint.Name ? null : name ?? "", goal == sprint.Goal ? null : goal ?? "", start == sprint.StartDate ? null : start, end == sprint.EndDate ? null : end);
        if (change is { Name: null, Goal: null, Start: null, End: null }) return null;
        return await RunAsync(user, sprint, (ctx, id) => writer.UpdateIterationAsync(ctx, id, change, ct), ct);
    }

    public Task<IResult?> StartAsync(Guid user, Sprint sprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        return sprint.SourceConnectionId is null || sprint.StartDate is not { } first || sprint.EndDate is not { } last
            ? Task.FromResult<IResult?>(null)
            : RunAsync(user, sprint, (ctx, id) => writer.StartIterationAsync(ctx, id, first, last, ct), ct);
    }

    /// <summary>
    /// Closing moves the unfinished issues first (to another mirrored sprint or the backlog), then closes. The two calls are not atomic: when the
    /// move reaches the tool and the close is refused, the tool keeps the move while nothing changes here; the next sync pass brings the issues
    /// here in line with the tool, and the close can be tried again (MirrorSprintEdgeTests).
    /// </summary>
    public async Task<IResult?> CloseAsync(Guid user, Sprint sprint, IReadOnlyList<Issue> unfinished, Sprint? moveTo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        ArgumentNullException.ThrowIfNull(unfinished);
        if (sprint.SourceConnectionId is null) return moveTo?.SourceConnectionId is null ? null : MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Scope);
        if (moveTo is not null && moveTo.SourceConnectionId != sprint.SourceConnectionId) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Scope);
        var keys = unfinished.Where(i => i.ExternalKey is not null).Select(i => i.ExternalKey!).ToList();
        var target = moveTo is null ? null : await gate.LinkOfAsync(LinkKinds.Sprint, moveTo.Id, ct);
        return await RunAsync(user, sprint, async (ctx, id) =>
        {
            if (keys.Count > 0 && target is not null) await writer.AddToIterationAsync(ctx, target.ExternalId, keys, ct);
            else if (keys.Count > 0) await writer.MoveToBacklogAsync(ctx, keys, ct);
            await writer.CloseIterationAsync(ctx, id, ct);
            await RefreshStampsAsync(ctx, unfinished, ct);
        }, ct);
    }

    public Task<IResult?> DeleteAsync(Guid user, Sprint sprint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        return sprint.SourceConnectionId is null ? Task.FromResult<IResult?>(null) : RunAsync(user, sprint, (ctx, id) => writer.DeleteIterationAsync(ctx, id, ct), ct);
    }

    public async Task<IResult?> AddIssuesAsync(Guid user, Sprint sprint, IReadOnlyList<Issue> issues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        ArgumentNullException.ThrowIfNull(issues);
        var mirroredIssues = issues.Count(i => i.ExternalKey is not null);
        if (sprint.SourceConnectionId is null)
        {
            // A local sprint takes no mirrored issue while its project is mirrored; in a detached project the issues keep their key but are Podium's.
            var projectMirrored = mirroredIssues > 0 && await db.Projects.AsNoTracking().AnyAsync(p => p.Id == sprint.ProjectId && p.SourceConnectionId != null, ct);
            return projectMirrored ? MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Scope) : null;
        }

        if (mirroredIssues != issues.Count) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Scope);
        var keys = issues.Select(i => i.ExternalKey!).ToList();
        return await RunAsync(user, sprint, async (ctx, id) =>
        {
            await writer.AddToIterationAsync(ctx, id, keys, ct);
            await RefreshStampsAsync(ctx, issues, ct);
        }, ct);
    }

    public async Task<IResult?> RemoveIssueAsync(Guid user, Sprint sprint, Issue issue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        ArgumentNullException.ThrowIfNull(issue);
        if (sprint.SourceConnectionId is null || issue.ExternalKey is null) return null;
        var key = issue.ExternalKey;
        return await RunAsync(user, sprint, async (ctx, _) =>
        {
            await writer.MoveToBacklogAsync(ctx, [key], ct);
            await RefreshStampsAsync(ctx, [issue], ct);
        }, ct);
    }

    /// <summary>Moving an issue changes it in the tool; the stamps stored here follow, so the next edit is not met with a conflict it caused itself.</summary>
    private async Task RefreshStampsAsync(TrackerContext ctx, IEnumerable<Issue> moved, CancellationToken ct)
    {
        var ids = moved.Where(i => i.ExternalKey is not null).Select(i => i.Id).ToList();
        if (ids.Count == 0) return;
        var links = await db.IntegrationLinks.Where(l => l.Kind == LinkKinds.Issue && ids.Contains(l.LocalId)).ToListAsync(ct);
        foreach (var link in links)
        {
            if (link.ExternalKey is { } key) link.ExternalUpdated = await writer.ItemStampAsync(ctx, key, ct);
        }
    }

    private async Task<IResult?> RunAsync(Guid user, Sprint sprint, Func<TrackerContext, string, Task> work, CancellationToken ct)
    {
        var (open, refusal) = await gate.OpenAsync(sprint.SourceConnectionId!.Value, user, ct);
        if (open is null) return refusal;
        var link = await gate.LinkOfAsync(LinkKinds.Sprint, sprint.Id, ct);
        if (link is null) return MirrorGate.Problem(StatusCodes.Status409Conflict, "mirrored_detached");
        try
        {
            await work(open.Ctx, link.ExternalId);
        }
        catch (TrackerException e)
        {
            return MirrorGate.Refusal(e);
        }

        return null;
    }
}
