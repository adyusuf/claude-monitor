using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// The access-policy enforcement switch in the workspace settings (ADR-0020 §4 rule 4): admins flip it, a change needs
/// re-authentication and has its own audit row, the same value or another setting does not.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessEnforcementTests(ApiFactory api)
{
    private static readonly TimeSpan PastWindow = TimeSpan.FromMinutes(11); // the re-authentication window is ten minutes

    private static string SettingsUrl(TestUser user) => $"/api/workspaces/{user.WorkspaceId}/settings";

    private static Task<HttpResponseMessage> SetAsync(TestUser user, bool on, string? code = null) =>
        user.SendAsync(HttpMethod.Put, SettingsUrl(user), new { enforceAccessPolicies = on, code });

    private static async Task<bool> EnforcedAsync(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}")).GetProperty("settings").GetProperty("enforceAccessPolicies").GetBoolean();

    private async Task<List<AuditEvent>> ChangesAsync(Guid workspaceId)
    {
        await using var db = api.Db();
        return await db.AuditEvents.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.Action == AuditActions.AccessEnforcementChanged).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task A_sign_in_older_than_the_window_cannot_change_it()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-stale");
        api.Clock.Advance(PastWindow);

        var response = await SetAsync(owner, true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RemoteChecks.ReauthRequired, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.False(await EnforcedAsync(owner));
        Assert.Empty(await ChangesAsync(owner.WorkspaceId));
    }

    [Fact]
    public async Task A_fresh_sign_in_turns_it_on_and_the_audit_row_says_before_after_and_how_it_passed()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-on");

        var response = await SetAsync(owner, true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await EnforcedAsync(owner));
        var row = Assert.Single(await ChangesAsync(owner.WorkspaceId));
        Assert.Equal(owner.Id, row.ActorUserId);
        var detail = row.Detail!.RootElement;
        Assert.Equal((false, true, ReauthWays.SignIn),
            (detail.GetProperty("before").GetBoolean(), detail.GetProperty("after").GetBoolean(), detail.GetProperty("reauth").GetString()));
    }

    [Fact]
    public async Task Turning_it_off_again_adds_a_second_row_that_reads_the_other_way_round()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-off");
        (await SetAsync(owner, true)).EnsureSuccessStatusCode();

        var response = await SetAsync(owner, false);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await EnforcedAsync(owner));
        var after = (await ChangesAsync(owner.WorkspaceId)).Select(r => r.Detail!.RootElement.GetProperty("after").GetBoolean());
        Assert.Equal([true, false], after);
    }

    [Theory]
    [InlineData("""{"enforceAccessPolicies":false}""")]
    [InlineData("""{"retentionDays":30}""")]
    public async Task A_save_that_leaves_the_switch_as_it_is_needs_no_re_authentication_and_writes_no_enforcement_row(string body)
    {
        var owner = await api.NewClient().SignedUpAsync("enf-same");
        api.Clock.Advance(PastWindow);

        var response = await owner.SendAsync(HttpMethod.Put, SettingsUrl(owner), JsonDocument.Parse(body).RootElement);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ChangesAsync(owner.WorkspaceId));
    }

    [Fact]
    public async Task Sending_the_value_it_already_has_after_the_window_changes_nothing_and_adds_no_row()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-again");
        (await SetAsync(owner, true)).EnsureSuccessStatusCode();
        api.Clock.Advance(PastWindow);

        var response = await SetAsync(owner, true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await ChangesAsync(owner.WorkspaceId));
        Assert.True(await EnforcedAsync(owner));
    }

    [Fact]
    public async Task With_two_step_sign_in_on_an_old_session_needs_a_valid_code_and_the_row_says_code()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-totp");
        var secret = await RemoteKit.EnableTotpAsync(api, owner);
        api.Clock.Advance(PastWindow);

        var wrong = await SetAsync(owner, true, "000000");
        var missing = await SetAsync(owner, true);
        var valid = await SetAsync(owner, true, RemoteKit.TotpNow(api, secret));

        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.NoContent], [wrong.StatusCode, missing.StatusCode, valid.StatusCode]);
        var row = Assert.Single(await ChangesAsync(owner.WorkspaceId));
        Assert.Equal(ReauthWays.Code, row.Detail!.RootElement.GetProperty("reauth").GetString());
    }

    [Fact]
    public async Task A_change_inside_the_code_window_passes_without_a_code_and_the_row_says_code_window()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-window");
        var secret = await RemoteKit.EnableTotpAsync(api, owner);
        api.Clock.Advance(PastWindow);
        (await SetAsync(owner, true, RemoteKit.TotpNow(api, secret))).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromMinutes(1)); // inside the five-minute code window, outside the sign-in window

        var response = await SetAsync(owner, false);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await EnforcedAsync(owner));
        var ways = (await ChangesAsync(owner.WorkspaceId)).Select(r => r.Detail!.RootElement.GetProperty("reauth").GetString());
        Assert.Equal([ReauthWays.Code, ReauthWays.CodeWindow], ways);
    }

    [Fact]
    public async Task A_refused_re_authentication_leaves_the_other_fields_of_the_same_body_unchanged()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-refused");
        var before = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings");
        var retention = before.GetProperty("retentionDays").GetInt32() + 1;
        var mask = !before.GetProperty("maskSecrets").GetBoolean();
        api.Clock.Advance(PastWindow);

        var response = await owner.SendAsync(HttpMethod.Put, SettingsUrl(owner), new { enforceAccessPolicies = true, retentionDays = retention, maskSecrets = mask });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings");
        Assert.Equal((before.GetProperty("retentionDays").GetInt32(), before.GetProperty("maskSecrets").GetBoolean(), false),
            (after.GetProperty("retentionDays").GetInt32(), after.GetProperty("maskSecrets").GetBoolean(), after.GetProperty("enforceAccessPolicies").GetBoolean()));
        Assert.Empty(await ChangesAsync(owner.WorkspaceId));
    }

    [Fact]
    public async Task An_admin_who_is_not_an_owner_can_change_it()
    {
        var owner = await api.NewClient().SignedUpAsync("enf-owner");
        var admin = await RemoteKit.MemberAsync(api, owner, "enf-admin", "admin");

        var response = await admin.SendAsync(HttpMethod.Put, SettingsUrl(owner), new { enforceAccessPolicies = true });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await EnforcedAsync(owner));
        Assert.Equal(admin.Id, Assert.Single(await ChangesAsync(owner.WorkspaceId)).ActorUserId);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task A_member_or_viewer_gets_404_and_nothing_changes(string role)
    {
        var owner = await api.NewClient().SignedUpAsync("enf-ws");
        var other = await RemoteKit.MemberAsync(api, owner, "enf-other", role);

        var response = await other.SendAsync(HttpMethod.Put, SettingsUrl(owner), new { enforceAccessPolicies = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await EnforcedAsync(owner));
        Assert.Empty(await ChangesAsync(owner.WorkspaceId));
    }
}
