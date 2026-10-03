using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Security;

/// <summary>
/// An agent's tokens: a short access token and a single-use refresh token, both opaque and stored hashed.
/// Presenting a refresh token that was already replaced means it was copied: every token of that agent is
/// revoked and the agent must be connected again.
/// </summary>
public static class AgentTokens
{
    public static TokenResponse Issue(MonitorDb db, ApiConfig config, Agent agent, DateTimeOffset now, AgentToken? replaces = null)
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
            AgentId = agent.Id,
            Kind = AgentTokenKinds.Refresh,
            TokenHash = Secrets.Hash(refresh),
            CreatedAt = now,
            ExpiresAt = now + config.AgentRefreshLifetime,
        };
        db.AgentTokens.AddRange(accessRow, refreshRow);
        if (replaces is not null)
        {
            replaces.ReplacedBy = refreshRow.Id;
            replaces.LastUsedAt = now;
        }

        return new TokenResponse(access, refresh, accessRow.ExpiresAt, refreshRow.ExpiresAt, agent.Id, agent.WorkspaceId);
    }

    public enum RefreshOutcome
    {
        Issued,
        Invalid,
        Reused,
    }

    public static async Task<(RefreshOutcome Outcome, TokenResponse? Tokens, Guid? AgentId)> RefreshAsync(
        MonitorDb db, ApiConfig config, string? refreshToken, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var hash = Secrets.Hash(refreshToken ?? "");
        var row = await db.AgentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == AgentTokenKinds.Refresh, ct);
        if (row is null)
        {
            return (RefreshOutcome.Invalid, null, null);
        }

        if (row.ReplacedBy is not null)
        {
            await RevokeAllAsync(db, row.AgentId, now, ct);
            return (RefreshOutcome.Reused, null, row.AgentId);
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == row.AgentId, ct);
        if (row.RevokedAt is not null || row.ExpiresAt <= now || agent is not { Status: AgentStatuses.Active })
        {
            return (RefreshOutcome.Invalid, null, row.AgentId);
        }

        return (RefreshOutcome.Issued, Issue(db, config, agent, now, row), agent.Id);
    }

    public static Task RevokeAllAsync(MonitorDb db, Guid agentId, DateTimeOffset now, CancellationToken ct) =>
        db.AgentTokens.Where(t => t.AgentId == agentId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
}
