using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Projects;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>
/// Writes a Podium change of a mirrored sprint, or of the issues in one, to the tool first (ADR-0015 §7). Same contract as <see cref="IssueWrites"/>:
/// null means go on with the local change, anything else is the refusal. A sprint and the issues in it must come from the same connection.
/// </summary>
public sealed class SprintWrites(MonitorDb db, IWorkWriter writer, MirrorGate gate)
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

    public async Task<IResult?> UpdateAsync(Guid user, Sprint sprint, string? name, string? goal, DateOnly? start, DateOnly? end, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        if (sprint.SourceConnectionId is null) return null;
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

    /// <summary>Closing moves the unfinished issues first (to another mirrored sprint or the backlog), then closes.</summary>
    public async Task<IResult?> CloseAsync(Guid user, Sprint sprint, IReadOnlyList<Issue> unfinished, Sprint? moveTo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sprint);
        ArgumentNullException.ThrowIfNull(unfinished);
        if (sprint.SourceConnectionId is null) return null;
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
        if (sprint.SourceConnectionId is null) return mirroredIssues == 0 ? null : MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Scope);
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
