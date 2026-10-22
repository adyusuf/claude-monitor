using Podium.Api.Data;
using Podium.Api.Projects;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>What an issue held before an edit, to see what changed.</summary>
public sealed record IssueFacts(string Type, string Title, string? Description, string? Priority, string? Estimate, string Status, Guid? AssigneeUserId, Guid? AssigneePersonId, Guid? ParentId)
{
    public static IssueFacts Of(Issue i) => new(i.Type, i.Title, i.Description, i.Priority, i.Estimate, i.Status, i.AssigneeUserId, i.AssigneePersonId, i.ParentId);
}

/// <summary>
/// Writes a Podium change of a mirrored issue to the tool first (ADR-0015 §7). Each method answers null when the local change may go on (also when
/// the issue is not mirrored or only Podium-only fields changed), or the refusal to send back. The tool's new change value is stored on the
/// link, so the echo of our own write is recognised. Nothing here saves; the endpoint's save carries it.
/// </summary>
public sealed class IssueWrites(PodiumDb db, IWorkWriter writer, MirrorGate gate)
{
    public async Task<IResult?> UpdateAsync(Guid user, ManagedProject project, IssueFacts before, Issue after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (after.ExternalKey is null || project.SourceConnectionId is not { } connectionId) return null;
        if (before.Type != after.Type) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.TypeLocked);
        var (change, unmapped) = await DiffAsync(connectionId, project, before, after, ct);
        if (unmapped is not null) return unmapped;
        var statusChanged = before.Status != after.Status;
        if (change.IsEmpty && !statusChanged) return null;
        var (open, refusal) = await gate.OpenAsync(connectionId, user, ct);
        if (open is null) return refusal;
        var link = await gate.LinkOfAsync(LinkKinds.Issue, after.Id, ct);
        try
        {
            if (link?.ExternalUpdated is { } known && await writer.ItemStampAsync(open.Ctx, after.ExternalKey, ct) != known) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Changed);
            if (statusChanged && !await writer.TransitionItemAsync(open.Ctx, after.ExternalKey, after.Status, ct))
            {
                return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.TransitionUnavailable);
            }

            await writer.UpdateItemAsync(open.Ctx, after.ExternalKey, change, ct);
            if (link is not null) link.ExternalUpdated = await writer.ItemStampAsync(open.Ctx, after.ExternalKey, ct);
        }
        catch (TrackerException e)
        {
            return MirrorGate.Refusal(e);
        }

        return null;
    }

    /// <summary>A quick status change (the sprint page).</summary>
    public async Task<IResult?> StatusAsync(Guid user, ManagedProject project, Issue issue, string newStatus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.ExternalKey is null || project.SourceConnectionId is not { } connectionId || issue.Status == newStatus) return null;
        var (open, refusal) = await gate.OpenAsync(connectionId, user, ct);
        if (open is null) return refusal;
        var link = await gate.LinkOfAsync(LinkKinds.Issue, issue.Id, ct);
        try
        {
            if (link?.ExternalUpdated is { } known && await writer.ItemStampAsync(open.Ctx, issue.ExternalKey, ct) != known) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Changed);
            if (!await writer.TransitionItemAsync(open.Ctx, issue.ExternalKey, newStatus, ct)) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.TransitionUnavailable);
            if (link is not null) link.ExternalUpdated = await writer.ItemStampAsync(open.Ctx, issue.ExternalKey, ct);
        }
        catch (TrackerException e)
        {
            return MirrorGate.Refusal(e);
        }

        return null;
    }

    /// <summary>A new issue in a mirrored project is made in the tool first, then given its key and link here.</summary>
    public async Task<IResult?> CreateAsync(Guid user, ManagedProject project, Issue issue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(issue);
        if (project.SourceConnectionId is not { } connectionId) return null;
        var (open, refusal) = await gate.OpenAsync(connectionId, user, ct);
        if (open is null) return refusal;
        var space = await gate.ProjectKeyAsync(project.Id, ct);
        if (space is null) return MirrorGate.Problem(StatusCodes.Status409Conflict, "mirrored_detached");
        var empty = new IssueFacts(issue.Type, "", null, null, null, IssueStatuses.Backlog, null, null, null);
        var (diff, unmapped) = await DiffAsync(connectionId, project, empty, issue, ct);
        if (unmapped is not null) return unmapped;
        var change = diff with { Summary = issue.Title };
        try
        {
            var (id, key) = await writer.CreateItemAsync(open.Ctx, new NewItem(space, issue.Type, change), ct);
            // A new issue starts in the tool's first status; if the workflow has no step to the wanted one, the row says what the tool says.
            if (issue.Status is not (IssueStatuses.Backlog or IssueStatuses.Ready) && !await writer.TransitionItemAsync(open.Ctx, key, issue.Status, ct)) issue.Status = IssueStatuses.Backlog;
            issue.ExternalKey = key;
            issue.ExternalUrl = $"https://{open.Connection.SiteHost}/browse/{Uri.EscapeDataString(key)}";
            db.IntegrationLinks.Add(new IntegrationLink
            {
                ConnectionId = connectionId,
                Kind = LinkKinds.Issue,
                ExternalId = id,
                ExternalKey = key,
                LocalId = issue.Id,
                ExternalUpdated = await writer.ItemStampAsync(open.Ctx, key, ct),
                SyncedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (TrackerException e)
        {
            return MirrorGate.Refusal(e);
        }

        return null;
    }

    /// <summary>A move in the backlog is a rank change in the tool.</summary>
    public async Task<IResult?> RankAsync(Guid user, ManagedProject project, Issue issue, Issue? after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.ExternalKey is null || project.SourceConnectionId is not { } connectionId) return null;
        if (after is not null && after.ExternalKey is null) return MirrorGate.Problem(StatusCodes.Status409Conflict, MirrorGate.Scope);
        var (open, refusal) = await gate.OpenAsync(connectionId, user, ct);
        if (open is null) return refusal;
        var first = after is null
            ? await db.Issues.AsNoTracking().Where(i => i.ProjectId == issue.ProjectId && i.Id != issue.Id && i.SprintId == null && i.ExternalKey != null && i.ArchivedAt == null)
                .OrderBy(i => i.Position).Select(i => i.ExternalKey).FirstOrDefaultAsync(ct)
            : null;
        if (after is null && first is null) return null;
        try
        {
            await writer.RankItemAsync(open.Ctx, issue.ExternalKey, after?.ExternalKey, first, ct);
        }
        catch (TrackerException e)
        {
            return MirrorGate.Refusal(e);
        }

        return null;
    }

    private async Task<(ItemChange Change, IResult? Refusal)> DiffAsync(Guid connectionId, ManagedProject project, IssueFacts before, Issue after, CancellationToken ct)
    {
        var change = new ItemChange { Summary = after.Title == before.Title ? null : after.Title };
        if (after.Description != before.Description) change = change with { SetDescription = true, Description = after.Description };
        if (after.Priority != before.Priority) change = change with { SetPriority = true, PriorityRank = after.Priority };
        if (project.EstimationUnit == EstimationUnits.StoryPoints && after.Estimate != before.Estimate) change = change with { SetEstimate = true, Estimate = after.Estimate };
        if (after.ParentId != before.ParentId)
        {
            var parentKey = after.ParentId is { } parent ? await db.Issues.AsNoTracking().Where(i => i.Id == parent).Select(i => i.ExternalKey).FirstOrDefaultAsync(ct) : null;
            change = change with { SetParent = true, ParentKey = parentKey };
        }

        if (after.AssigneeUserId != before.AssigneeUserId)
        {
            // A person who is assigned in the tool but is no member shows as "nobody" in the form; sending that back must not unassign them.
            var untouched = after.AssigneeUserId is null && before.AssigneeUserId is null;
            if (!untouched)
            {
                var account = after.AssigneeUserId is { } who ? await gate.AccountOfAsync(connectionId, who, ct) : null;
                if (after.AssigneeUserId is not null && account is null) return (change, MirrorGate.Problem(StatusCodes.Status400BadRequest, "assignee_not_in_jira"));
                change = change with { SetAssignee = true, AssigneeAccountId = account };
                after.AssigneePersonId = account is null ? null
                    : await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == connectionId && p.ExternalAccountId == account).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            }
        }

        return (change, null);
    }
}
