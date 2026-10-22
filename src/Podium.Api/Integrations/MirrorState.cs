using Podium.Api.Data;
using Podium.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>How many rows a run created, changed, archived or found (the numbers of a sync run; never any text).</summary>
public sealed class MirrorCounts
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Archived { get; set; }
    public int People { get; set; }
}

/// <summary>
/// What a run needs to know about one mirrored project, loaded once under the project's row lock: the links to the tool's rows, the issues and
/// sprints they point at, and the next number and position to give. Changes are made on these tracked rows and saved by the caller.
/// </summary>
public sealed class MirrorState
{
    public required IntegrationConnection Connection { get; init; }
    public required ManagedProject Project { get; init; }
    public Dictionary<string, IntegrationLink> IssueLinks { get; } = [];
    public Dictionary<string, IntegrationLink> SprintLinks { get; } = [];
    public Dictionary<Guid, Issue> Issues { get; } = [];
    public Dictionary<Guid, Sprint> Sprints { get; } = [];

    /// <summary>Members who may be given work (member role or above); a viewer can be matched to a person but never be an assignee.</summary>
    public HashSet<Guid> Workers { get; } = [];
    public int NextIssueNumber { get; set; } = 1;
    public int NextSprintNumber { get; set; } = 1;
    public double NextPosition { get; set; } = IssueEndpoints.Step;

    /// <summary>Takes the project's row lock (the one issue and sprint creation take), so a number is never given twice.</summary>
    public static async Task<MirrorState> LoadAsync(PodiumDb db, IntegrationConnection connection, ManagedProject project, CancellationToken ct)
    {
        await IssueEndpoints.LockProjectAsync(db, project.Id, ct);
        var state = new MirrorState { Connection = connection, Project = project };
        var links = await db.IntegrationLinks.Where(l => l.ConnectionId == connection.Id && (l.Kind == LinkKinds.Issue || l.Kind == LinkKinds.Sprint)).ToListAsync(ct);
        var issues = await db.Issues.Where(i => i.ProjectId == project.Id).ToListAsync(ct);
        var sprints = await db.Sprints.Where(s => s.ProjectId == project.Id).ToListAsync(ct);
        var workers = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == project.WorkspaceId && m.RemovedAt == null
            && (m.Role == Roles.Owner || m.Role == Roles.Admin || m.Role == Roles.Member)).Select(m => m.UserId).ToListAsync(ct);
        state.Workers.UnionWith(workers);
        foreach (var i in issues) state.Issues[i.Id] = i;
        foreach (var s in sprints) state.Sprints[s.Id] = s;
        foreach (var l in links)
        {
            if (l.Kind == LinkKinds.Issue && state.Issues.ContainsKey(l.LocalId)) state.IssueLinks[l.ExternalId] = l;
            if (l.Kind == LinkKinds.Sprint && state.Sprints.ContainsKey(l.LocalId)) state.SprintLinks[l.ExternalId] = l;
        }

        state.NextIssueNumber = (issues.Count == 0 ? 0 : issues.Max(i => i.Number)) + 1;
        state.NextSprintNumber = (sprints.Count == 0 ? 0 : sprints.Max(s => s.Number)) + 1;
        state.NextPosition = (issues.Count == 0 ? 0 : issues.Max(i => i.Position)) + IssueEndpoints.Step;
        return state;
    }

    public Issue? IssueOfKey(string key) =>
        IssueLinks.Values.FirstOrDefault(l => l.ExternalKey == key) is { } link ? Issues.GetValueOrDefault(link.LocalId) : null;
}
