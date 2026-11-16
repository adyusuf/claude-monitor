using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>
/// A permission request as the agent that made it sees it again: by its own key (F-188), when a create is sent again after a lost response, and the
/// answer it carries (with the question tool's chosen options, F-196), for the create's answer and the obsolete poll.
/// </summary>
public static class PermissionRequestKeys
{
    /// <summary>No key (an older agent), or one shaped as a run's key is: 1 to 100 characters, none a control character.</summary>
    public static bool Valid(string? clientKey) =>
        clientKey is null || (clientKey is { Length: >= 1 and <= RunRules.ClientKeyMax } && !clientKey.Any(char.IsControl));

    /// <summary>
    /// The request this agent already made with this key, answered as a create is (the same id, its expiry, and its answer when the agent reads
    /// one inline); null without a key or when there is none yet.
    /// </summary>
    public static async Task<PermissionRequestCreated?> FindAsync(PodiumDb db, Guid agentId, string? clientKey, string externalId, bool inline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (clientKey is null) return null;
        var row = await db.PermissionRequests.AsNoTracking().FirstOrDefaultAsync(p => p.AgentId == agentId && p.ClientKey == clientKey, ct);
        return row is null ? null : new PermissionRequestCreated(row.Id, row.ExpiresAt, inline ? AnswerOf(row, externalId) : null);
    }

    /// <summary>The request's answer as the agent takes it; null while nobody has answered.</summary>
    public static PermissionAnswerMessage? AnswerOf(PermissionRequest row, string externalId)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Decision is null ? null : new PermissionAnswerMessage(row.Id, externalId, row.Decision, row.Reason);
    }
}
