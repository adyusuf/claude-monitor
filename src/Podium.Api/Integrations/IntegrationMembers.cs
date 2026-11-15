using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>
/// What a person who leaves a workspace (or is removed from it) takes along from its work tracker connections, as an account deletion does for
/// every workspace: their Atlassian sign-ins and sign-ins under way, their link to a tool person, and the connections only they read with need a
/// new sign-in (ADR-0015 §5). Runs inside the removal's transaction.
/// </summary>
public static class IntegrationMembers
{
    public static async Task ForRemovedMemberAsync(PodiumDb db, Guid workspaceId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connections = db.IntegrationConnections.Where(c => c.WorkspaceId == workspaceId).Select(c => c.Id);
        await db.IntegrationOAuthStates.Where(s => s.UserId == userId && s.WorkspaceId == workspaceId).ExecuteDeleteAsync(ct);
        await db.IntegrationAccounts.Where(a => a.UserId == userId && connections.Contains(a.ConnectionId)).ExecuteDeleteAsync(ct);
        await db.IntegrationPeople.Where(p => p.UserId == userId && connections.Contains(p.ConnectionId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, (Guid?)null), ct);
        await db.IntegrationConnections.Where(c => c.WorkspaceId == workspaceId && c.ConnectorUserId == userId && c.Status == ConnectionStatuses.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ConnectionStatuses.Reauthorize).SetProperty(c => c.UpdatedAt, now), ct);
    }
}
