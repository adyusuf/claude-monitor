using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Security;

/// <summary>
/// An agent's tokens: a short access token and a single-use refresh token, both opaque and stored hashed.
/// Presenting a refresh token that was already replaced means it was copied: every token of that agent is
/// revoked and the agent must be connected again. One exception, a lost answer (F-206): see <see cref="GraceAsync"/>.
/// </summary>
public static class AgentTokens
{
    public static TokenResponse Issue(PodiumDb db, ApiConfig config, Agent agent, DateTimeOffset now) =>
        Issue(db, config, agent, now, Guid.CreateVersion7());

    private static TokenResponse Issue(PodiumDb db, ApiConfig config, Agent agent, DateTimeOffset now, Guid refreshId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agent);
        var access = Secrets.NewToken();
        var refresh = Secrets.NewToken();
        var accessRow = new AgentToken
        {
            AgentId = agent.Id,
            Kind = AgentTokenKinds.Access,
            TokenHash = Secrets.Hash(access),
            CreatedAt = now,
            ExpiresAt = now + config.AgentAccessLifetime,
        };
        var refreshRow = new AgentToken
        {
            Id = refreshId,
            AgentId = agent.Id,
            Kind = AgentTokenKinds.Refresh,
            TokenHash = Secrets.Hash(refresh),
            CreatedAt = now,
            ExpiresAt = now + config.AgentRefreshLifetime,
        };
        db.AgentTokens.AddRange(accessRow, refreshRow);
        return new TokenResponse(access, refresh, accessRow.ExpiresAt, refreshRow.ExpiresAt, agent.Id, agent.WorkspaceId);
    }

    public enum RefreshOutcome
    {
        Issued,
        Invalid,
        Reused,
    }

    public static async Task<(RefreshOutcome Outcome, TokenResponse? Tokens, Guid? AgentId, string? Reason)> RefreshAsync(
        PodiumDb db, ApiConfig config, string? refreshToken, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var hash = Secrets.Hash(refreshToken ?? "");
        var row = await db.AgentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == AgentTokenKinds.Refresh, ct);
        if (row is null)
        {
            return (RefreshOutcome.Invalid, null, null, RefreshReasons.Unknown);
        }

        if (row.ReplacedBy is { } replacedBy)
        {
            if (await GraceAsync(db, config, row, replacedBy, now, ct) is { } again)
            {
                return (RefreshOutcome.Issued, again, row.AgentId, null);
            }

            await RevokeAllAsync(db, row.AgentId, now, ct);
            return (RefreshOutcome.Reused, null, row.AgentId, RefreshReasons.Reused);
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == row.AgentId, ct);
        if (row.RevokedAt is not null || row.ExpiresAt <= now || agent is not { Status: AgentStatuses.Active })
        {
            // Which of the three it was is told to the holder of the token (it already holds it): the agent's log then says what to do next.
            var reason = agent is not { Status: AgentStatuses.Active } ? RefreshReasons.AgentRevoked
                : row.RevokedAt is not null ? RefreshReasons.Revoked
                : RefreshReasons.Expired;
            return (RefreshOutcome.Invalid, null, row.AgentId, reason);
        }

        // Single use under a race: the replace is a compare-and-set on ReplacedBy IS NULL. The caller's transaction holds the row
        // until the successor is stored, so a second refresh with the same token waits and changes nothing. It read the token before
        // the first one committed, which is just what a lost-answer retry does (S-2): the committed row gets the same grace as above,
        // and only when that refuses is it a reuse.
        var successor = Guid.CreateVersion7();
        var claimed = await db.AgentTokens.Where(t => t.Id == row.Id && t.ReplacedBy == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedBy, successor).SetProperty(t => t.LastUsedAt, now), ct);
        if (claimed == 0)
        {
            var committed = await db.AgentTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == row.Id, ct);
            if (committed?.ReplacedBy is { } winner && await GraceAsync(db, config, committed, winner, now, ct) is { } again)
            {
                return (RefreshOutcome.Issued, again, row.AgentId, null);
            }

            await RevokeAllAsync(db, row.AgentId, now, ct);
            return (RefreshOutcome.Reused, null, row.AgentId, RefreshReasons.Reused);
        }

        return (RefreshOutcome.Issued, Issue(db, config, agent, now, successor), agent.Id, null);
    }

    /// <summary>
    /// A refresh answer lost after the commit leaves the agent with the replaced token (F-206). Within
    /// <see cref="ApiConfig.AgentRefreshGrace"/> of the replace, and only while the successor refresh token was never replaced (the use of
    /// its access token is not tracked), the replaced token is honoured once: the successor is marked replaced by a new pair (so whoever
    /// shows it later is judged a reuse) and its access token ends. A second try, a replaced successor, a late try or a revoked agent get
    /// null: the caller treats it as a reuse.
    /// The successor's mark is a compare-and-set, so of two tries at once one gets the pair and the other is a reuse.
    /// </summary>
    private static async Task<TokenResponse?> GraceAsync(PodiumDb db, ApiConfig config, AgentToken row, Guid successorId,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (row.RevokedAt is not null || row.ExpiresAt <= now || row.LastUsedAt is not { } replacedAt || now - replacedAt > config.AgentRefreshGrace)
        {
            return null;
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == row.AgentId && a.Status == AgentStatuses.Active, ct);
        var successor = await db.AgentTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == successorId, ct);
        if (agent is null || successor is null) return null;
        var next = Guid.CreateVersion7();
        var claimed = await db.AgentTokens.Where(t => t.Id == successorId && t.ReplacedBy == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedBy, next), ct);
        if (claimed == 0) return null;
        // The lost pair's access token was issued with its refresh token, at the same instant.
        await db.AgentTokens.Where(t => t.AgentId == row.AgentId && t.Kind == AgentTokenKinds.Access && t.CreatedAt == successor.CreatedAt
                && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return Issue(db, config, agent, now, next);
    }

    public static Task RevokeAllAsync(PodiumDb db, Guid agentId, DateTimeOffset now, CancellationToken ct) =>
        db.AgentTokens.Where(t => t.AgentId == agentId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
}
