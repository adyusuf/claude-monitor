using System.Net;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Only the newest reset link works (F-030): a new Forgot ends the older open links, and a successful reset ends every other open link
/// of the account, so a link in an old mail cannot change the password again after the owner has changed it.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ResetLinkLifecycleTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> ResetAsync(TestUser client, string token, string password) =>
        client.PostAsync("/api/auth/password/reset", new { token, password });

    [Fact]
    public async Task A_new_reset_link_ends_the_older_one()
    {
        var user = await api.NewClient().SignedUpAsync("reset-older");
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var older = api.Mail.TokenFor(user.Email);
        api.Clock.Advance(TimeSpan.FromMinutes(10)); // past the one-mail-per-address window (F-175)
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var newer = api.Mail.TokenFor(user.Email);

        Assert.NotEqual(older, newer);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(client, older, "an older link password")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, newer, "the newest link password")).StatusCode);
    }

    [Fact]
    public async Task A_reset_ends_every_other_open_link_of_the_account()
    {
        var user = await api.NewClient().SignedUpAsync("reset-others");
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var mailed = api.Mail.TokenFor(user.Email);
        var other = Secrets.NewToken(); // an open link the Forgot did not end (one written before this rule, say)
        await using (var db = api.Db())
        {
            var now = api.Clock.GetUtcNow();
            db.UserTokens.Add(new UserToken
            {
                UserId = user.Id,
                Purpose = TokenPurposes.ResetPassword,
                TokenHash = Secrets.Hash(other),
                CreatedAt = now,
                ExpiresAt = now + TimeSpan.FromHours(24),
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, mailed, "the password I chose")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(client, other, "a password from an old mail")).StatusCode);
        await using var check = api.Db();
        Assert.False(await check.UserTokens.AnyAsync(t => t.UserId == user.Id && t.Purpose == TokenPurposes.ResetPassword && t.UsedAt == null));
    }
}
