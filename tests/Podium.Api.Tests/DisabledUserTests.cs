using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// No admin path sets <see cref="UserStatuses.Disabled"/> yet (F-038), but the check constraint allows it and a manual change or a later tool may:
/// every way in must then stay closed. One test per guard, the status written directly.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class DisabledUserTests(ApiFactory api)
{
    private async Task DisableAsync(Guid userId)
    {
        await using var db = api.Db();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatuses.Disabled));
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in_with_the_right_password()
    {
        var user = await api.NewClient().SignedUpAsync("disabled-login");
        await DisableAsync(user.Id);

        var response = await api.NewClient().PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_disabled_users_open_session_stops_working()
    {
        var user = await api.NewClient().SignedUpAsync("disabled-session");
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/me")).StatusCode);
        await DisableAsync(user.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_user_cannot_finish_the_second_step()
    {
        var user = await api.NewClient().SignedUpAsync("disabled-mfa");
        var token = Secrets.NewToken();
        await using (var db = api.Db())
        {
            db.UserTokens.Add(new UserToken
            {
                UserId = user.Id,
                Purpose = TokenPurposes.MfaPending,
                TokenHash = Secrets.Hash(token),
                CreatedAt = api.Clock.GetUtcNow(),
                ExpiresAt = api.Clock.GetUtcNow() + TimeSpan.FromMinutes(5),
            });
            await db.SaveChangesAsync();
        }

        await DisableAsync(user.Id);

        var response = await api.NewClient().PostAsync("/api/auth/mfa", new { token, code = "000000" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("invalid_token", errors.GetProperty("token")[0].GetString());
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in_with_a_linked_provider()
    {
        var subject = "gh-" + Guid.NewGuid();
        var header = $"{subject}|{Emails.New("disabled-provider")}|true|Provider Person";
        var first = api.NewClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=signin"))
        {
            request.Headers.Add(TestExternal.Header, header);
            Assert.Equal(HttpStatusCode.Redirect, (await first.Http.SendAsync(request)).StatusCode);
        }

        var id = (await first.GetJsonAsync("/api/me")).GetProperty("id").GetGuid();
        await DisableAsync(id);

        var again = api.NewClient();
        using var retry = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/github/done?mode=signin");
        retry.Headers.Add(TestExternal.Header, header);
        var response = await again.Http.SendAsync(retry);

        Assert.Contains("/login?error=account_disabled", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_user_gets_no_reset_link()
    {
        var user = await api.NewClient().SignedUpAsync("disabled-forgot");
        await DisableAsync(user.Id);
        var before = api.Mail.Sent.Count(m => string.Equals(m.To, user.Email, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(HttpStatusCode.Accepted, (await api.NewClient().PostAsync("/api/auth/password/forgot", new { email = user.Email })).StatusCode);

        Assert.Equal(before, api.Mail.Sent.Count(m => string.Equals(m.To, user.Email, StringComparison.OrdinalIgnoreCase)));
    }
}
