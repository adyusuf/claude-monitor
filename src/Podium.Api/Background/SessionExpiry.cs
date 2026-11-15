using Podium.Api.Data;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Background;

/// <summary>
/// Expires the commands and permission requests of sessions that ran out of time, and tells each workspace's live stream (the same message the
/// web gets when one of them changes), so an open session page does not keep a queued message or an open permission card until another event comes.
/// </summary>
public static class SessionExpiry
{
    public static async Task CommandsAsync(PodiumDb db, DateTimeOffset now, Broker? broker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var due = await db.SessionCommands.AsNoTracking()
            .Where(c => (c.Status == CommandStatuses.Queued || c.Status == CommandStatuses.Delivered) && c.ExpiresAt <= now)
            .Select(c => new { c.Id, c.SessionId, c.WorkspaceId }).ToListAsync(ct);
        if (due.Count == 0) return;
        foreach (var d in due)
        {
            // Each row by compare-and-set with the condition of the read: one answered or cancelled since is not touched, and not announced.
            var changed = await db.SessionCommands
                .Where(c => c.Id == d.Id && (c.Status == CommandStatuses.Queued || c.Status == CommandStatuses.Delivered) && c.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatuses.Expired), ct);
            if (changed != 1) continue;
            broker?.Publish(Broker.Workspace(d.WorkspaceId), new StreamMessage("command", new { sessionId = d.SessionId, id = d.Id, status = CommandStatuses.Expired }));
        }
    }

    public static async Task PermissionsAsync(PodiumDb db, DateTimeOffset now, Broker? broker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var due = await db.PermissionRequests.AsNoTracking().Where(p => p.Status == PermissionStatuses.Open && p.ExpiresAt <= now)
            .Select(p => new { p.Id, p.SessionId, p.WorkspaceId }).ToListAsync(ct);
        if (due.Count == 0) return;
        foreach (var d in due)
        {
            var changed = await db.PermissionRequests.Where(p => p.Id == d.Id && p.Status == PermissionStatuses.Open && p.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PermissionStatuses.Expired), ct);
            if (changed != 1) continue;
            broker?.Publish(Broker.Workspace(d.WorkspaceId), new StreamMessage("permission", new { sessionId = d.SessionId, id = d.Id, status = PermissionStatuses.Expired }));
        }
    }
}
