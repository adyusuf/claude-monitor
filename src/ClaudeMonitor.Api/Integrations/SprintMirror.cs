using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>Creates and updates the Podium sprints that mirror the tool's (ADR-0015 §4).</summary>
public sealed class SprintMirror(MonitorDb db, TimeProvider clock)
{
    public const int NameMax = 100;
    public const int GoalMax = 1000;

    public Sprint Upsert(MirrorState state, ExternalIteration it, MirrorCounts counts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(it);
        var now = clock.GetUtcNow();
        Sprint sprint;
        IntegrationLink link;
        if (state.SprintLinks.TryGetValue(it.Id, out var existing) && state.Sprints.TryGetValue(existing.LocalId, out var found))
        {
            sprint = found;
            link = existing;
            counts.Updated++;
        }
        else
        {
            sprint = new Sprint
            {
                WorkspaceId = state.Project.WorkspaceId,
                ProjectId = state.Project.Id,
                Number = state.NextSprintNumber++,
                SourceConnectionId = state.Connection.Id,
                CreatedBy = state.Connection.ConnectorUserId,
                CreatedAt = now,
            };
            db.Sprints.Add(sprint);
            state.Sprints[sprint.Id] = sprint;
            link = new IntegrationLink { ConnectionId = state.Connection.Id, Kind = LinkKinds.Sprint, ExternalId = it.Id, LocalId = sprint.Id };
            db.IntegrationLinks.Add(link);
            state.SprintLinks[it.Id] = link;
            counts.Created++;
        }

        var status = JiraMapping.Sprint(it.State);
        sprint.Name = JiraMapping.Text(it.Name, NameMax);
        sprint.Goal = JiraMapping.Text(it.Goal, GoalMax);
        sprint.StartDate = it.Start;
        sprint.EndDate = it.End is { } end && it.Start is { } start && end < start ? start : it.End;
        sprint.Status = status;
        sprint.StartedAt = status == SprintStatuses.Planned ? null : sprint.StartedAt ?? now;
        sprint.ClosedAt = status == SprintStatuses.Closed ? sprint.ClosedAt ?? now : null;
        sprint.ArchivedAt = null;
        sprint.UpdatedBy = state.Connection.ConnectorUserId;
        sprint.UpdatedAt = now;
        link.ExternalUpdated = $"{it.State}|{it.Name}|{it.Start}|{it.End}";
        link.SyncedAt = now;
        return sprint;
    }

    /// <summary>A sprint deleted in the tool is archived here; its issues go back to the backlog.</summary>
    public void Archive(MirrorState state, IntegrationLink link)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(link);
        if (!state.Sprints.TryGetValue(link.LocalId, out var sprint) || sprint.ArchivedAt is not null) return;
        var now = clock.GetUtcNow();
        sprint.ArchivedAt = now;
        sprint.UpdatedAt = now;
        foreach (var issue in state.Issues.Values.Where(i => i.SprintId == sprint.Id))
        {
            issue.SprintId = null;
            if (issue.Status is IssueStatuses.InProgress or IssueStatuses.InReview) issue.Status = IssueStatuses.Ready;
        }
    }
}
