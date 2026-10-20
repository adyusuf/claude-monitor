using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>Creates and updates the Podium project that mirrors a space (ADR-0015 §4).</summary>
public sealed class ProjectMirror(MonitorDb db, TimeProvider clock)
{
    public const int NameMax = 200;

    /// <summary>The project for <paramref name="space"/>: updated in place when it was imported before, else created and owned by <paramref name="owner"/>.</summary>
    public async Task<ManagedProject> UpsertAsync(IntegrationConnection connection, ExternalContainer space, Guid owner, MirrorCounts counts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(space);
        var now = clock.GetUtcNow();
        var link = await db.IntegrationLinks.FirstOrDefaultAsync(l => l.ConnectionId == connection.Id && l.Kind == LinkKinds.Project && l.ExternalId == space.Id, ct);
        var project = link is null ? null : await db.Projects.FirstOrDefaultAsync(p => p.Id == link.LocalId, ct);
        var name = await FreeNameAsync(connection.WorkspaceId, space, project?.Id, ct);
        if (project is null)
        {
            project = new ManagedProject
            {
                WorkspaceId = connection.WorkspaceId,
                OwnerUserId = owner,
                CreatedBy = owner,
                CreatedAt = now,
                ItemTypes = [.. WorkItemTypes.All],
                PriorityScale = PriorityScales.OneToFive,
                EstimationUnit = EstimationUnits.StoryPoints,
                SourceConnectionId = connection.Id,
            };
            db.Projects.Add(project);
            link = new IntegrationLink { ConnectionId = connection.Id, Kind = LinkKinds.Project, ExternalId = space.Id, LocalId = project.Id };
            db.IntegrationLinks.Add(link);
            counts.Created++;
        }
        else
        {
            counts.Updated++;
        }

        project.Name = name;
        project.NameSearch = SearchText.Normalize(name);
        project.Purpose = JiraMapping.Text(space.Description, 2000);
        project.SourceConnectionId = connection.Id;
        project.Status = ManagedStatuses.Active; // a space that is imported again comes back
        project.UpdatedBy = owner;
        project.UpdatedAt = now;
        link!.ExternalKey = space.Key;
        link.SyncedAt = now;
        await db.SaveChangesAsync(ct);
        return project;
    }

    /// <summary>The space's name, or "Name (KEY)" when another active project of the workspace already has it.</summary>
    private async Task<string> FreeNameAsync(Guid workspaceId, ExternalContainer space, Guid? self, CancellationToken ct)
    {
        var name = JiraMapping.Text(space.Name, NameMax) ?? space.Key;
        if (!await Taken(name)) return name;
        var withKey = $"{name[..Math.Min(name.Length, NameMax - space.Key.Length - 3)]} ({space.Key})";
        return withKey;

        async Task<bool> Taken(string candidate)
        {
            var search = SearchText.Normalize(candidate);
            return await db.Projects.AnyAsync(p => p.WorkspaceId == workspaceId && p.NameSearch == search && p.Status == ManagedStatuses.Active && p.Id != self, ct);
        }
    }
}
