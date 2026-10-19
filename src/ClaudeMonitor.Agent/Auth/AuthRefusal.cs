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

    private const string Login = "Run `aipod login --server <url>` to connect it again.";

    public static string Message(string? reason) => reason switch
    {
        RefreshReasons.Expired => $"the API refused this agent: its credentials expired ({reason}). {Login}",
        RefreshReasons.Revoked or RefreshReasons.AgentRevoked =>
            $"the API refused this agent: it was revoked ({reason}), on the web or by connecting this machine again. {Login}",
        RefreshReasons.Reused =>
            $"the API revoked this agent because its credentials were used twice ({reason}): another copy of this agent, or a restored copy of its home folder, is probably running. " +
            $"Stop the other copy first, then on this machine: {Login}",
        RefreshReasons.Unknown =>
            $"the API does not know these credentials ({reason}): the agent was deleted on the web or the server was reset. {Login}",
        Net.ApiClient.NoRefreshToken => $"no refresh token is stored on this machine ({reason}). {Login}",
        { Length: > 0 } other when other.StartsWith("http_", StringComparison.Ordinal) =>
            $"the API refused to refresh this agent's credentials ({other}; the server gave no reason, it may be an older version). {Login}",
        _ => $"the API refused this agent (no reason given). {Login}",
    };
}
