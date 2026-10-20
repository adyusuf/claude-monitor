using System.Collections.Concurrent;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>
/// Holds the sealed Atlassian tokens: gives a valid access token (refreshing under a lock, because a refresh token rotates and a second
/// parallel refresh would burn the new one) and marks an account for sign-in again when Atlassian refuses it (ADR-0015 §5).
/// </summary>
public sealed class IntegrationTokens(MonitorDb db, IServiceScopeFactory scopes, ApiConfig config, JiraOAuth oauth, TimeProvider clock)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    public static string Seal(ApiConfig config, string plain) => SecretBox.Seal(config.IntegrationKey!, System.Text.Encoding.UTF8.GetBytes(plain));

    public static string Open(ApiConfig config, string sealedText) => System.Text.Encoding.UTF8.GetString(SecretBox.Open(config.IntegrationKey!, sealedText));

    /// <summary>The context for calls made with this user's own account, or null when the user has none on this connection.</summary>
    public async Task<TrackerContext?> ContextForAsync(IntegrationConnection connection, Guid userId, CancellationToken ct)
    {
        var account = await db.IntegrationAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ConnectionId == connection.Id && a.UserId == userId && a.Status == AccountStatuses.Active, ct);
        return account is null ? null : new TrackerContext(connection.CloudId, token => AccessAsync(account.Id, token));
    }

    /// <summary>The context of the connector, whose account reads and syncs for everybody; null when it needs to sign in again.</summary>
    public Task<TrackerContext?> ConnectorContextAsync(IntegrationConnection connection, CancellationToken ct) =>
        connection.Status == ConnectionStatuses.Active ? ContextForAsync(connection, connection.ConnectorUserId, ct) : Task.FromResult<TrackerContext?>(null);

    public async Task<string> AccessAsync(Guid accountId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The token rows are read and saved in a context of their own: saving them must never carry along what the caller
            // has changed and not yet decided to keep (an edit that the tool is about to refuse).
            await using var scope = scopes.CreateAsyncScope();
            var own = scope.ServiceProvider.GetRequiredService<MonitorDb>();
            // Read again under the lock: another request may have refreshed while this one waited.
            var account = await own.IntegrationAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
                          ?? throw new TrackerException(TrackerException.Codes.Unauthorized);
            if (account.Status != AccountStatuses.Active) throw new TrackerException(TrackerException.Codes.Unauthorized);
            var now = clock.GetUtcNow();
            if (account.AccessExpiresAt - now > Skew) return Open(config, account.AccessToken);
            try
            {
                var tokens = await oauth.RefreshAsync(Open(config, account.RefreshToken), ct);
                account.AccessToken = Seal(config, tokens.Access);
                account.RefreshToken = Seal(config, tokens.Refresh);
                account.AccessExpiresAt = now.AddSeconds(tokens.ExpiresInSeconds);
                account.UpdatedAt = now;
                await own.SaveChangesAsync(ct);
                return tokens.Access;
            }
            catch (TrackerException e) when (e.Code == TrackerException.Codes.Unauthorized)
            {
                await MarkReauthorizeAsync(own, account, now, ct);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task MarkReauthorizeAsync(MonitorDb db, IntegrationAccount account, DateTimeOffset now, CancellationToken ct)
    {
        account.Status = AccountStatuses.Reauthorize;
        account.UpdatedAt = now;
        var connection = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.Id == account.ConnectionId, ct);
        if (connection is not null && connection.ConnectorUserId == account.UserId && connection.Status == ConnectionStatuses.Active)
        {
            connection.Status = ConnectionStatuses.Reauthorize;
            connection.LastError = TrackerException.Codes.Unauthorized;
            connection.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
