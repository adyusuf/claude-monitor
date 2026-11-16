using System.Net;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Background;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-185: a web sign-in that expired or was revoked is kept a while, then deleted by housekeeping; a live or recently ended one stays.
/// Deleting the account clears the browser line (user_agent) of every sign-in that is left.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class LoginSessionPurgeTests(ApiFactory api)
{
    private const string Browser = "purge-test-browser/1.0";
    private static readonly TimeSpan Beyond = Housekeeper.KeepEndedLoginSessions + TimeSpan.FromDays(1);
    private static readonly TimeSpan Within = Housekeeper.KeepEndedLoginSessions - TimeSpan.FromDays(1);

    private static LoginSession Row(Guid userId, DateTimeOffset expires, DateTimeOffset? revoked = null) => new()
    {
        UserId = userId,
        TokenHash = "purge-" + Guid.NewGuid().ToString("N"),
        CreatedAt = expires - TimeSpan.FromDays(30),
        LastSeenAt = expires - TimeSpan.FromDays(30),
        ExpiresAt = expires,
        RevokedAt = revoked,
        UserAgent = Browser,
    };

    private async Task<List<Guid>> LeftAsync(Guid userId)
    {
        await using var check = api.Db();
        return await check.LoginSessions.Where(s => s.UserId == userId && s.TokenHash.StartsWith("purge-")).Select(s => s.Id).ToListAsync();
    }

    [Fact]
    public async Task Sign_ins_ended_longer_ago_than_the_keep_period_are_deleted_and_live_or_recently_ended_ones_stay_on_every_pass()
    {
        var user = await api.NewClient().SignedUpAsync("purge-login-sessions");
        var now = api.Clock.GetUtcNow();
        var longExpired = Row(user.Id, now - Beyond);
        var longRevoked = Row(user.Id, now + TimeSpan.FromDays(10), now - Beyond);
        var recentlyExpired = Row(user.Id, now - Within);
        var recentlyRevoked = Row(user.Id, now + TimeSpan.FromDays(10), now - Within);
        var live = Row(user.Id, now + TimeSpan.FromDays(10));
        await using (var db = api.Db())
        {
            db.LoginSessions.AddRange(longExpired, longRevoked, recentlyExpired, recentlyRevoked, live);
            await db.SaveChangesAsync();
        }

        var kept = new[] { recentlyExpired.Id, recentlyRevoked.Id, live.Id }.Order().ToList();
        await using (var db = api.Db())
        {
            await Housekeeper.RunOnceAsync(db, now, CancellationToken.None);
        }

        Assert.Equal(kept, (await LeftAsync(user.Id)).Order());

        // A second pass finds nothing more to delete.
        await using (var db = api.Db())
        {
            await Housekeeper.RunOnceAsync(db, now, CancellationToken.None);
        }

        Assert.Equal(kept, (await LeftAsync(user.Id)).Order());

        // The live sign-in of the account still works after the passes.
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public void The_keep_period_is_thirty_days()
    {
        Assert.Equal(TimeSpan.FromDays(30), Housekeeper.KeepEndedLoginSessions);
    }

    [Fact]
    public async Task Deleting_the_account_clears_the_browser_line_of_its_sign_ins_and_leaves_other_peoples()
    {
        var user = await api.NewClient().SignedUpAsync("purge-login-ua");
        var other = await api.NewClient().SignedUpAsync("purge-login-ua-other");
        var now = api.Clock.GetUtcNow();
        var revokedEarlier = Row(user.Id, now + TimeSpan.FromDays(10), now - TimeSpan.FromDays(1));
        await using (var db = api.Db())
        {
            db.LoginSessions.Add(revokedEarlier);
            await db.LoginSessions.Where(s => s.UserId == user.Id || s.UserId == other.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserAgent, Browser));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        await using var check = api.Db();
        var mine = await check.LoginSessions.AsNoTracking().Where(s => s.UserId == user.Id).ToListAsync();
        Assert.True(mine.Count >= 2);
        Assert.All(mine, s => Assert.Null(s.UserAgent));
        Assert.All(mine, s => Assert.NotNull(s.RevokedAt));
        // A sign-in revoked before the deletion keeps the time it was revoked.
        Assert.Equal(revokedEarlier.RevokedAt, mine.Single(s => s.Id == revokedEarlier.Id).RevokedAt);
        Assert.All(await check.LoginSessions.AsNoTracking().Where(s => s.UserId == other.Id).ToListAsync(), s => Assert.Equal(Browser, s.UserAgent));
    }
}
