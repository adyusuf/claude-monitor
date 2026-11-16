using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>What a workspace settings save needs beyond admin rights.</summary>
public static class WorkspaceSettingsRules
{
    /// <summary>409 title: the settings changed since the page that is saving them was loaded (<c>expectedUpdatedAt</c> no longer matches).</summary>
    public const string StaleUpdate = "stale_update";

    /// <summary>
    /// True when the save lowers the retention days (housekeeping then deletes the finished runs with their output, asks, actions, resolved alerts
    /// and unused approval rules older than the new value, with no archive) or turns secret masking off (what is captured from then on keeps its
    /// secrets): both need re-authentication like the access enforcement switch (F-219). Raising either, the same value and every other
    /// setting do not.
    /// </summary>
    public static bool NeedsReauth(WorkspaceSettings current, SettingsRequest req)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(req);
        return req.RetentionDays is { } days && days < current.RetentionDays || req.MaskSecrets == false && current.MaskSecrets;
    }

    /// <summary>
    /// Locks the workspace's settings row until the transaction ends, so two saves of the same page cannot both pass the stale check (the
    /// second waits, then reads what the first wrote). The workspace settings and the remote settings are one row.
    /// </summary>
    public static async Task LockAsync(PodiumDb db, Guid workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.WorkspaceSettings.Where(x => x.WorkspaceId == workspaceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaskSecrets, x => x.MaskSecrets), ct);
    }

    /// <summary>
    /// True when the page sent the <paramref name="expected"/> time it read and the row has changed since. Absent means the page does not
    /// check (an older client: the last save wins, as before). The database keeps microseconds, so a difference below one is no change.
    /// </summary>
    public static bool IsStale(DateTimeOffset current, DateTimeOffset? expected) =>
        expected is { } e && Math.Abs((current - e).Ticks) >= TimeSpan.TicksPerMicrosecond;

    public static IResult Stale() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: StaleUpdate);
}
