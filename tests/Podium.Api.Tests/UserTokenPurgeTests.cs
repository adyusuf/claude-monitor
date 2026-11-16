using Microsoft.EntityFrameworkCore;
using Podium.Api.Background;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-185 (with F-031): expired or spent verification, reset and two-step pending tokens are kept a while, then deleted by housekeeping.</summary>
[Collection(ApiGroup.Name)]
public sealed class UserTokenPurgeTests(ApiFactory api)
{
    private static readonly TimeSpan Beyond = Housekeeper.KeepEndedUserTokens + TimeSpan.FromDays(1);
    private static readonly TimeSpan Within = Housekeeper.KeepEndedUserTokens - TimeSpan.FromDays(1);

    [Fact]
    public async Task Tokens_expired_or_used_longer_ago_than_the_keep_period_are_deleted_and_the_rest_stay()
    {
        var user = await api.NewClient().SignedUpAsync("purge-user-tokens");
        var now = api.Clock.GetUtcNow();
        UserToken Row(string purpose, DateTimeOffset expires, DateTimeOffset? used = null) => new()
        {
            UserId = user.Id,
            Purpose = purpose,
            TokenHash = "purge-" + Guid.NewGuid().ToString("N"),
            CreatedAt = expires - TimeSpan.FromDays(1),
            ExpiresAt = expires,
            UsedAt = used,
        };
        var longExpired = Row(TokenPurposes.VerifyEmail, now - Beyond);
        var longUsed = Row(TokenPurposes.ResetPassword, now + TimeSpan.FromHours(1), now - Beyond);
        var longExpiredPending = Row(TokenPurposes.MfaPending, now - Beyond);
        var recentlyExpired = Row(TokenPurposes.VerifyEmail, now - Within);
        var recentlyUsed = Row(TokenPurposes.ResetPassword, now + TimeSpan.FromHours(1), now - Within);
        var live = Row(TokenPurposes.ResetPassword, now + TimeSpan.FromHours(1));
        await using (var db = api.Db())
        {
            db.UserTokens.AddRange(longExpired, longUsed, longExpiredPending, recentlyExpired, recentlyUsed, live);
            await db.SaveChangesAsync();
        }

        await using (var db = api.Db())
        {
            await Housekeeper.RunOnceAsync(db, now, CancellationToken.None);
        }

        await using var check = api.Db();
        var left = await check.UserTokens.Where(t => t.UserId == user.Id && t.TokenHash.StartsWith("purge-")).Select(t => t.Id).ToListAsync();
        Assert.Equal(new[] { recentlyExpired.Id, recentlyUsed.Id, live.Id }.Order(), left.Order());
    }

    [Fact]
    public void The_keep_period_is_seven_days()
    {
        Assert.Equal(TimeSpan.FromDays(7), Housekeeper.KeepEndedUserTokens);
    }
}
