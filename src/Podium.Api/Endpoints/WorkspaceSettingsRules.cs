using Podium.Api.Data;

namespace Podium.Api.Endpoints;

/// <summary>What a workspace settings save needs beyond admin rights.</summary>
public static class WorkspaceSettingsRules
{
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
}
