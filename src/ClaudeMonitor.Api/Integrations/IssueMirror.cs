using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>Creates, updates and archives the Podium issues that mirror the tool's (ADR-0015 §4). Works on a loaded <see cref="MirrorState"/>; the caller saves.</summary>
public sealed class IssueMirror(MonitorDb db, TimeProvider clock)
{
    private const string SubtaskNote = "Sub-task of ";

    /// <summary>Makes the Podium issue match <paramref name="item"/>. Returns it, or null when nothing changed since the last sync.</summary>
    public Issue? Upsert(MirrorState state, ExternalItem item, IReadOnlyDictionary<string, IntegrationPerson> people, string siteHost, MirrorCounts counts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);
        var now = clock.GetUtcNow();
        Issue issue;
        if (state.IssueLinks.TryGetValue(item.Id, out var link) && state.Issues.TryGetValue(link.LocalId, out var existing))
        {
            // Unchanged in the tool and still placed where it belongs here: nothing to do. A sprint or an epic imported after the issue
            // changes where it belongs without changing the issue, so those are compared too.
            if (link.ExternalUpdated == item.UpdatedStamp && existing.ArchivedAt is null
                && existing.SprintId == SprintOf(state, item)?.Id && existing.ParentId == ParentOf(state, item, existing.Id)) return null;
            issue = existing;
            counts.Updated++;
        }
        else
        {
            issue = new Issue
            {
                WorkspaceId = state.Project.WorkspaceId,
                ProjectId = state.Project.Id,
                Number = state.NextIssueNumber++,
                Position = state.NextPosition,
                CreatedBy = state.Connection.ConnectorUserId,
                CreatedAt = now,
            };
            state.NextPosition += IssueEndpoints.Step;
            db.Issues.Add(issue);
            state.Issues[issue.Id] = issue;
            link = new IntegrationLink { ConnectionId = state.Connection.Id, Kind = LinkKinds.Issue, ExternalId = item.Id, LocalId = issue.Id };
            db.IntegrationLinks.Add(link);
            state.IssueLinks[item.Id] = link;
            counts.Created++;
        }

        Fill(issue, state, item, people, siteHost, now);
        link.ExternalKey = item.Key;
        link.ExternalUpdated = item.UpdatedStamp;
        link.SyncedAt = now;
        return issue;
    }

    /// <summary>The issues of the tool that are gone (or no longer visible) are archived, never deleted.</summary>
    public int ArchiveMissing(MirrorState state, ISet<string> stillThere)
    {
        var now = clock.GetUtcNow();
        var archived = 0;
        foreach (var (externalId, link) in state.IssueLinks)
        {
            if (stillThere.Contains(externalId) || !state.Issues.TryGetValue(link.LocalId, out var issue) || issue.ArchivedAt is not null) continue;
            issue.ArchivedAt = now;
            issue.UpdatedAt = now;
            archived++;
        }

        return archived;
    }

    private static void Fill(Issue issue, MirrorState state, ExternalItem item, IReadOnlyDictionary<string, IntegrationPerson> people, string siteHost, DateTimeOffset now)
    {
        var type = JiraMapping.Type(item.TypeName, item.IsSubtask);
        issue.Title = JiraMapping.Text(item.Summary, JiraMapping.TitleMax) ?? item.Key;
        issue.TitleSearch = SearchText.Normalize(issue.Title);
        issue.Type = state.Project.ItemTypes.Contains(type) ? type : state.Project.ItemTypes[0];
        issue.Priority = JiraMapping.Priority(item.PriorityName);
        issue.Estimate = state.Project.EstimationUnit == EstimationUnits.StoryPoints ? item.Estimate : null;
        var sprint = SprintOf(state, item);
        issue.SprintId = sprint?.Id;
        issue.Status = JiraMapping.Status(item.StatusCategory, item.StatusName, item.Resolution, inSprint: sprint is not null);
        issue.Description = Describe(item);
        issue.ExternalKey = item.Key;
        issue.ExternalUrl = $"https://{siteHost}/browse/{Uri.EscapeDataString(item.Key)}";
        issue.AssigneePersonId = item.Assignee is { } a && people.TryGetValue(a.Id, out var person) ? person.Id : null;
        issue.AssigneeUserId = item.Assignee is { } b && people.TryGetValue(b.Id, out var who) ? who.UserId : null;
        issue.ParentId = ParentOf(state, item, issue.Id);
        issue.ArchivedAt = null;
        issue.UpdatedBy = state.Connection.ConnectorUserId;
        issue.UpdatedAt = now;
    }

    /// <summary>The epic the issue hangs under, when the tool says so and that epic is here.</summary>
    private static Guid? ParentOf(MirrorState state, ExternalItem item, Guid self) =>
        item.ParentKey is { } parentKey && "Epic".Equals(item.ParentTypeName, StringComparison.OrdinalIgnoreCase)
        && state.IssueOfKey(parentKey) is { Type: WorkItemTypes.Epic } epic && epic.Id != self ? epic.Id : null;

    /// <summary>The sprint the issue sits in, when that sprint is imported and the issue still belongs to it (an unfinished issue of a closed sprint is back in the backlog).</summary>
    private static Sprint? SprintOf(MirrorState state, ExternalItem item)
    {
        if (item.Iteration is not { } it || !state.SprintLinks.TryGetValue(it.Id, out var link) || !state.Sprints.TryGetValue(link.LocalId, out var sprint)) return null;
        var done = item.StatusCategory.Equals("done", StringComparison.OrdinalIgnoreCase);
        return it.State.Equals("closed", StringComparison.OrdinalIgnoreCase) && !done ? null : sprint;
    }

    private static string? Describe(ExternalItem item)
    {
        var text = item.Description;
        if (item.IsSubtask && item.ParentKey is { } parent) text = $"{SubtaskNote}{parent}" + (text is null ? "" : "\n\n" + text);
        return JiraMapping.Text(text, JiraMapping.TextMax);
    }
}
