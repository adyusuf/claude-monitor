using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Saving on the server-wide page needs a fresh sign-in or a two-step code (ADR-0017); reading and the test mail do not.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsReauthTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private static readonly TimeSpan Past = TimeSpan.FromMinutes(11); // the window is ten minutes

    private async Task<bool> SavedAsync(string key)
    {
        await using var db = api.Db();
        return await db.ServerSettings.AnyAsync(s => s.Key == key);
    }

    private async Task<int> AuditedAsync(TestUser admin)
    {
        await using var db = api.Db();
        return await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged);
    }

    private static async Task AssertReauthRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("reauth_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_sign_in_older_than_the_window_cannot_save_until_the_person_signs_in_again()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-reauth");
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.PutAsync(admin, "smtp", new { host = "fresh.example.net" })).StatusCode); // just signed in
        api.Clock.Advance(Past);

        await AssertReauthRequiredAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { host = "stale.example.net" }));

        Assert.Equal(1, await AuditedAsync(admin)); // only the first save
        await using (var db = api.Db())
        {
            Assert.Equal("fresh.example.net", (await db.ServerSettings.AsNoTracking().SingleAsync(s => s.Key == "smtp.host")).Value);
        }

        // Reading and the test mail are not saves: an old sign-in still has them.
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.GetAsync(admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/server/integrations/smtp/test")).StatusCode);
        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = admin.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.PutAsync(again, "smtp", new { host = "again.example.net" })).StatusCode);
    }

    [Fact]
    public async Task A_sign_in_just_inside_the_window_still_saves()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-inside");
        api.Clock.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(HttpStatusCode.OK, (await ServerAdminKit.PutAsync(admin, "smtp", new { host = "inside.example.net" })).StatusCode);
    }

    [Fact]
    public async Task With_two_step_sign_in_an_old_session_saves_with_a_valid_code_and_not_with_a_wrong_or_missing_one()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-totp");
        var secret = await RemoteKit.EnableTotpAsync(api, admin);
        api.Clock.Advance(Past);

        await AssertReauthRequiredAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { host = "no-code.example.net" }));
        await AssertReauthRequiredAsync(await ServerAdminKit.PutAsync(admin, "smtp", new { host = "bad-code.example.net" }, "000000"));
        Assert.False(await SavedAsync("smtp.host"));

        var saved = await ServerAdminKit.PutAsync(admin, "smtp", new { host = "coded.example.net" }, RemoteKit.TotpNow(api, secret));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(await SavedAsync("smtp.host"));
    }

    [Fact]
    public async Task A_refused_save_for_a_bad_value_does_not_ask_for_a_fresh_sign_in_and_one_for_an_environment_value_neither()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ss-order");
        api.Clock.Advance(Past);

        Assert.Equal(HttpStatusCode.BadRequest, (await ServerAdminKit.PutAsync(admin, "smtp", new { port = "0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ServerAdminKit.PutAsync(admin, "github", new { client_id = "x" })).StatusCode);
        Assert.Equal(0, await AuditedAsync(admin));
    }
}
