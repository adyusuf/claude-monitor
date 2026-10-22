using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>The second half of the sign-in round trip: the code becomes tokens, the site and the account are checked, and the rows are written.</summary>
public static class IntegrationConnect
{
    public const string Ok = "ok";
    public const string SiteNotFound = "site_not_found";

    public static async Task<string> CompleteAsync(PodiumDb db, ApiConfig config, TimeProvider clock, JiraOAuth oauth, IntegrationOAuthState state,
        string code, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var tokens = await oauth.ExchangeAsync(code, IntegrationTokens.Open(config, state.Verifier), ct);
        var site = (await oauth.SitesAsync(tokens.Access, ct)).FirstOrDefault(s => s.Host == state.SiteHost);
        if (site is null) return SiteNotFound;
        var (accountId, displayName) = await oauth.MeAsync(site.Id, tokens.Access, ct);

        IntegrationConnection? connection;
        if (state.Purpose == OAuthPurposes.Account)
        {
            connection = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.Id == state.ConnectionId && c.WorkspaceId == state.WorkspaceId
                && c.Status != ConnectionStatuses.Disconnected, ct);
            if (connection is null || await Access.MemberAsync(db, state.UserId, state.WorkspaceId, Roles.Member, ct) is null) return "invalid_state";
            if (connection.CloudId != site.Id) return SiteNotFound;
        }
        else
        {
            if (await Access.MemberAsync(db, state.UserId, state.WorkspaceId, Roles.Admin, ct) is null) return "invalid_state";
            connection = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.WorkspaceId == state.WorkspaceId && c.Provider == TrackerProviders.Jira
                && c.SiteHost == state.SiteHost && c.Status != ConnectionStatuses.Disconnected, ct);
            if (connection is null)
            {
                connection = new IntegrationConnection
                {
                    WorkspaceId = state.WorkspaceId,
                    Provider = TrackerProviders.Jira,
                    SiteHost = state.SiteHost,
                    CreatedBy = state.UserId,
                    CreatedAt = now,
                };
                db.IntegrationConnections.Add(connection);
            }

            // Whoever connects (again) becomes the connector: it is how a connection that needs a new sign-in is repaired.
            connection.CloudId = site.Id;
            connection.ConnectorUserId = state.UserId;
            connection.Status = ConnectionStatuses.Active;
            connection.LastError = null;
            connection.UpdatedAt = now;
        }

        var account = await db.IntegrationAccounts.FirstOrDefaultAsync(a => a.ConnectionId == connection.Id && a.UserId == state.UserId, ct);
        if (account is null)
        {
            account = new IntegrationAccount { ConnectionId = connection.Id, UserId = state.UserId, CreatedAt = now };
            db.IntegrationAccounts.Add(account);
        }

        account.ExternalAccountId = accountId;
        account.DisplayName = displayName;
        account.RefreshToken = IntegrationTokens.Seal(config, tokens.Refresh);
        account.AccessToken = IntegrationTokens.Seal(config, tokens.Access);
        account.AccessExpiresAt = now.AddSeconds(tokens.ExpiresInSeconds);
        account.Status = AccountStatuses.Active;
        account.UpdatedAt = now;
        Audit.Add(db, http, clock, state.Purpose == OAuthPurposes.Account ? AuditActions.IntegrationAccountLinked : AuditActions.IntegrationConnected,
            state.WorkspaceId, state.UserId, targetType: "integration", targetId: connection.Id);
        await db.SaveChangesAsync(ct);
        return Ok;
    }
}
