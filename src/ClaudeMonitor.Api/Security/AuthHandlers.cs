using System.Security.Claims;
using System.Text.Encodings.Web;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClaudeMonitor.Api.Security;

public static class Schemes
{
    public const string Session = "session";
    public const string Agent = "agent";
    public const string External = "external";
}

public static class ClaimNames
{
    public const string UserId = "uid";
    public const string AgentId = "aid";
    public const string WorkspaceId = "wid";
    public const string LoginSessionId = "lsid";
}

/// <summary>The web: an opaque token in the session cookie, looked up hashed in login_sessions (revocable).</summary>
public sealed class SessionAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    MonitorDb db, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(ApiConfig.SessionCookie, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var now = clock.GetUtcNow();
        var hash = Secrets.Hash(token);
        var row = await (from s in db.LoginSessions
                         join u in db.Users on s.UserId equals u.Id
                         where s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now
                               && u.Status == UserStatuses.Active
                         select s).FirstOrDefaultAsync(Context.RequestAborted);
        if (row is null)
        {
            return AuthenticateResult.Fail("session not valid");
        }

        if (now - row.LastSeenAt > TimeSpan.FromMinutes(5))
        {
            row.LastSeenAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimNames.UserId, row.UserId.ToString()), new Claim(ClaimNames.LoginSessionId, row.Id.ToString())],
            Schemes.Session);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Schemes.Session));
    }
}

/// <summary>
/// An agent: "Authorization: Bearer &lt;access token&gt;", looked up hashed in agent_tokens. Its user must still be a
/// member of the agent's workspace and the workspace active (fail-closed, global #6).
/// </summary>
public sealed class AgentAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    MonitorDb db, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var now = clock.GetUtcNow();
        var hash = Secrets.Hash(header["Bearer ".Length..].Trim());
        var found = await (from t in db.AgentTokens
                           join a in db.Agents on t.AgentId equals a.Id
                           join u in db.Users on a.UserId equals u.Id
                           join m in db.WorkspaceMembers on new { a.WorkspaceId, a.UserId } equals new { m.WorkspaceId, m.UserId }
                           join w in db.Workspaces on a.WorkspaceId equals w.Id
                           where t.TokenHash == hash && t.Kind == AgentTokenKinds.Access && t.RevokedAt == null
                                 && t.ExpiresAt > now && a.Status == AgentStatuses.Active && u.Status == UserStatuses.Active
                                 && m.RemovedAt == null && w.Status == WorkspaceStatuses.Active
                           select new { t, a }).FirstOrDefaultAsync(Context.RequestAborted);
        if (found is null)
        {
            return AuthenticateResult.Fail("agent token not valid");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimNames.AgentId, found.a.Id.ToString()),
            new Claim(ClaimNames.UserId, found.a.UserId.ToString()),
            new Claim(ClaimNames.WorkspaceId, found.a.WorkspaceId.ToString()),
        ], Schemes.Agent);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Schemes.Agent));
    }
}

public static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimNames.UserId)!);

    public static Guid AgentId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimNames.AgentId)!);

    public static Guid AgentWorkspaceId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimNames.WorkspaceId)!);

    public static Guid? LoginSessionId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimNames.LoginSessionId), out var id) ? id : null;
}
