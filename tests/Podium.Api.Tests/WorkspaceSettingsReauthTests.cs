using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-219: lowering the retention days or turning secret masking off takes effect at once and deletes data, so it needs a recent sign-in (or a
/// valid two-step code) like the access-enforcement switch; raising either, the same value and every other setting do not.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceSettingsReauthTests(ApiFactory api)
{
    private static readonly TimeSpan PastWindow = TimeSpan.FromMinutes(11); // the re-authentication window is ten minutes

    private static string Url(TestUser user) => $"/api/workspaces/{user.WorkspaceId}/settings";

    private static async Task<JsonElement> SettingsAsync(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}")).GetProperty("settings");

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RemoteChecks.ReauthRequired, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_stale_sign_in_cannot_lower_the_retention_and_the_setting_is_kept()
    {
        var owner = await api.NewClient().SignedUpAsync("ret-low-stale");
        var days = (await SettingsAsync(owner)).GetProperty("retentionDays").GetInt32();
        api.Clock.Advance(PastWindow);

        await AssertRefusedAsync(await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = days - 1 }));

        Assert.Equal(days, (await SettingsAsync(owner)).GetProperty("retentionDays").GetInt32());
    }

    [Fact]
    public async Task A_stale_sign_in_cannot_turn_masking_off_and_the_setting_is_kept()
    {
        var owner = await api.NewClient().SignedUpAsync("mask-off-stale");
        api.Clock.Advance(PastWindow);

        await AssertRefusedAsync(await owner.SendAsync(HttpMethod.Put, Url(owner), new { maskSecrets = false }));

        Assert.True((await SettingsAsync(owner)).GetProperty("maskSecrets").GetBoolean());
    }

    [Fact]
    public async Task A_refused_lowering_leaves_the_other_fields_of_the_same_body_unchanged()
    {
        var owner = await api.NewClient().SignedUpAsync("ret-low-body");
        var before = await SettingsAsync(owner);
        api.Clock.Advance(PastWindow);

        await AssertRefusedAsync(await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 1, eventMaxBytes = 2048 }));

        var after = await SettingsAsync(owner);
        Assert.Equal((before.GetProperty("retentionDays").GetInt32(), before.GetProperty("eventMaxBytes").GetInt32()),
            (after.GetProperty("retentionDays").GetInt32(), after.GetProperty("eventMaxBytes").GetInt32()));
    }

    [Fact]
    public async Task A_fresh_sign_in_lowers_the_retention_and_turns_masking_off_and_the_audit_row_says_how_it_passed()
    {
        var owner = await api.NewClient().SignedUpAsync("ret-fresh");

        var response = await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 7, maskSecrets = false });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var settings = await SettingsAsync(owner);
        Assert.Equal((7, false), (settings.GetProperty("retentionDays").GetInt32(), settings.GetProperty("maskSecrets").GetBoolean()));
        await using var db = api.Db();
        var row = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.WorkspaceId == owner.WorkspaceId && a.Action == AuditActions.SettingsChanged);
        Assert.Equal(ReauthWays.SignIn, row.Detail!.RootElement.GetProperty("reauth").GetString());
    }

    [Fact]
    public async Task With_a_stale_sign_in_raising_the_retention_masking_on_the_same_values_and_other_settings_still_save()
    {
        var owner = await api.NewClient().SignedUpAsync("ret-allowed");
        (await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 30, maskSecrets = false })).EnsureSuccessStatusCode();
        api.Clock.Advance(PastWindow);

        var raise = await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 60, maskSecrets = true });
        var same = await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 60, maskSecrets = true });
        var other = await owner.SendAsync(HttpMethod.Put, Url(owner), new { eventMaxBytes = 4096, agentUpdate = "check", claudeUpdate = true });

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent], [raise.StatusCode, same.StatusCode, other.StatusCode]);
        var settings = await SettingsAsync(owner);
        Assert.Equal((60, true, 4096), (settings.GetProperty("retentionDays").GetInt32(), settings.GetProperty("maskSecrets").GetBoolean(), settings.GetProperty("eventMaxBytes").GetInt32()));
    }

    [Fact]
    public async Task With_two_step_sign_in_a_stale_session_lowers_the_retention_only_with_a_valid_code()
    {
        var owner = await api.NewClient().SignedUpAsync("ret-totp");
        var secret = await RemoteKit.EnableTotpAsync(api, owner);
        api.Clock.Advance(PastWindow);

        var wrong = await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 5, code = "000000" });
        var missing = await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 5 });
        var valid = await owner.SendAsync(HttpMethod.Put, Url(owner), new { retentionDays = 5, code = RemoteKit.TotpNow(api, secret) });

        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.NoContent], [wrong.StatusCode, missing.StatusCode, valid.StatusCode]);
        Assert.Equal(5, (await SettingsAsync(owner)).GetProperty("retentionDays").GetInt32());
    }
}
