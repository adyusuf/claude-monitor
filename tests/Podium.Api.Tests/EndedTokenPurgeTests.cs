using Microsoft.EntityFrameworkCore;
using Podium.Api.Background;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-210: agent tokens and device codes are kept a while after they end, then deleted by housekeeping.</summary>
[Collection(ApiGroup.Name)]
public sealed class EndedTokenPurgeTests(ApiFactory api)
{
    private static readonly TimeSpan Beyond = Housekeeper.KeepEndedCredentials + TimeSpan.FromDays(1);
    private static readonly TimeSpan Within = Housekeeper.KeepEndedCredentials - TimeSpan.FromDays(1);

    private async Task<Guid[]> TokenIdsAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.AgentTokens.Where(t => t.AgentId == agentId).Select(t => t.Id).ToArrayAsync();
    }

    private async Task PassAsync()
    {
        await using var db = api.Db();
        await Housekeeper.RunOnceAsync(db, api.Clock.GetUtcNow(), CancellationToken.None);
    }

    [Fact]
    public async Task Tokens_expired_or_revoked_for_longer_than_the_keep_period_are_deleted_and_the_rest_stay()
    {
        var user = await api.NewClient().SignedUpAsync("purge-tokens");
        var now = api.Clock.GetUtcNow();
        var longExpired = (await user.ConnectAgentAsync()).Tokens.AgentId;
        var recentlyExpired = (await user.ConnectAgentAsync()).Tokens.AgentId;
        var longRevoked = (await user.ConnectAgentAsync()).Tokens.AgentId;
        var recentlyRevoked = (await user.ConnectAgentAsync()).Tokens.AgentId;
        var live = (await user.ConnectAgentAsync()).Tokens.AgentId;
        await using (var db = api.Db())
        {
            await db.AgentTokens.Where(t => t.AgentId == longExpired).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, now - Beyond));
            await db.AgentTokens.Where(t => t.AgentId == recentlyExpired).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, now - Within));
            await db.AgentTokens.Where(t => t.AgentId == longRevoked).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now - Beyond));
            await db.AgentTokens.Where(t => t.AgentId == recentlyRevoked).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now - Within));
        }

        await PassAsync();

        Assert.Empty(await TokenIdsAsync(longExpired));
        Assert.Empty(await TokenIdsAsync(longRevoked));
        Assert.Equal(2, (await TokenIdsAsync(recentlyExpired)).Length);
        Assert.Equal(2, (await TokenIdsAsync(recentlyRevoked)).Length);
        Assert.Equal(2, (await TokenIdsAsync(live)).Length);
    }

    [Fact]
    public async Task Device_codes_that_ended_longer_ago_than_the_keep_period_are_deleted()
    {
        var now = api.Clock.GetUtcNow();
        Guid old, recent;
        await using (var db = api.Db())
        {
            DeviceAuthorization Row(DateTimeOffset expires) => new()
            {
                DeviceCodeHash = "purge-" + Guid.NewGuid().ToString("N"),
                UserCode = "PURGE-" + Guid.NewGuid().ToString("N")[..6],
                MachineKeyHash = "purge",
                CreatedAt = expires - TimeSpan.FromMinutes(15),
                ExpiresAt = expires,
                Status = DeviceStatuses.Consumed,
            };
            var oldRow = Row(now - Beyond);
            var recentRow = Row(now - Within);
            db.DeviceAuthorizations.AddRange(oldRow, recentRow);
            await db.SaveChangesAsync();
            (old, recent) = (oldRow.Id, recentRow.Id);
        }

        await PassAsync();

        await using var check = api.Db();
        Assert.False(await check.DeviceAuthorizations.AnyAsync(d => d.Id == old));
        Assert.True(await check.DeviceAuthorizations.AnyAsync(d => d.Id == recent));
    }
}
