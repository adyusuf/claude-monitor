using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>
/// What the daemon says when the API refuses to refresh its credentials: the reason the API gave and the next step for the person at the
/// machine. The reason is a closed code (never a token); the daemon also keeps it in the database so `aipod status` can show it.
/// </summary>
public static class AuthRefusal
{
    public const string ReasonKey = "auth.refused_reason";
    public const string AtKey = "auth.refused_at";

    /// <summary>For the log and the model: always English.</summary>
    public static string Message(string? reason)
    {
        using var english = Strings.Scope("en");
        return Describe(reason);
    }

    /// <summary>For the person at the terminal (`aipod status`, the tray): in their language.</summary>
    public static string Describe(string? reason)
    {
        var login = Strings.T(Msg.AuLogin);
        return reason switch
        {
            RefreshReasons.Expired => Strings.T(Msg.AuExpired, reason, login),
            RefreshReasons.Revoked or RefreshReasons.AgentRevoked => Strings.T(Msg.AuRevoked, reason, login),
            RefreshReasons.Reused => Strings.T(Msg.AuReused, reason, login),
            RefreshReasons.Unknown => Strings.T(Msg.AuUnknown, reason, login),
            Net.ApiClient.NoRefreshToken => Strings.T(Msg.AuNoToken, reason, login),
            { Length: > 0 } other when other.StartsWith("http_", StringComparison.Ordinal) => Strings.T(Msg.AuHttp, other, login),
            _ => Strings.T(Msg.AuNoReason, login),
        };
    }
}
