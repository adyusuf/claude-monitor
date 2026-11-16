using System.Net;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-081: disconnecting deletes every person's Atlassian tokens, so it needs what connecting needs: a recent sign-in, or a two-step code. An
/// old client calls DELETE with no body and still disconnects on a recent sign-in; POST .../disconnect carries the code. The automatic-invitation
/// switch stays without one.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationDisconnectReauthTests(ApiFactory api)
{
    private static readonly TimeSpan PastWindow = TimeSpan.FromMinutes(11); // the re-authentication window is ten minutes

    private async Task<(string Status, int Accounts)> StateAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return ((await JiraKit.ConnectionAsync(w)).Status, await db.IntegrationAccounts.CountAsync(a => a.ConnectionId == w.ConnectionId));
    }

    [Fact]
    public async Task An_old_sign_in_without_two_step_is_refused_with_reauth_required_and_nothing_changes()
    {
        var w = await JiraKit.ConnectedAsync(api);
        await JiraKit.AgeSignInAsync(api, w.Admin);

        await RemoteKit.AssertProblemAsync(await w.Admin.SendAsync(HttpMethod.Delete, w.Base), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await RemoteKit.AssertProblemAsync(await w.Admin.PostAsync($"{w.Base}/disconnect", new { code = "123456" }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);

        Assert.Equal((ConnectionStatuses.Active, 1), await StateAsync(w));
        await using var db = api.Db();
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == AuditActions.IntegrationDisconnected));
    }

    [Fact]
    public async Task With_two_step_on_an_old_sign_in_a_missing_or_wrong_code_is_refused_and_a_valid_code_disconnects()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, w.Admin);
        api.Clock.Advance(PastWindow);

        var missing = await w.Admin.PostAsync($"{w.Base}/disconnect", new { });
        var wrong = await w.Admin.PostAsync($"{w.Base}/disconnect", new { code = "000000" });
        var oldAddress = await w.Admin.SendAsync(HttpMethod.Delete, w.Base); // carries no code: refused the same way

        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden], [missing.StatusCode, wrong.StatusCode, oldAddress.StatusCode]);
        Assert.Equal((ConnectionStatuses.Active, 1), await StateAsync(w));

        var valid = await w.Admin.PostAsync($"{w.Base}/disconnect", new { code = RemoteKit.TotpNow(api, secret) });

        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.Equal((ConnectionStatuses.Disconnected, 0), await StateAsync(w));
        await using var db = api.Db();
        var row = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == AuditActions.IntegrationDisconnected);
        Assert.Equal(ReauthWays.Code, row.Detail!.RootElement.GetProperty("reauth").GetString());
    }

    [Fact]
    public async Task A_recent_sign_in_disconnects_at_the_old_address_and_the_new_one_without_a_code_and_the_invite_switch_needs_no_code()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var old = await JiraKit.ConnectedAsync(api);
        await JiraKit.AgeSignInAsync(api, old.Admin);

        // The switch for automatic invitations stays as it was: no re-authentication.
        Assert.Equal(HttpStatusCode.NoContent, (await old.Admin.SendAsync(HttpMethod.Put, old.Base, new { autoInvite = true })).StatusCode);
        Assert.True((await JiraKit.ConnectionAsync(old)).AutoInvite);

        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.SendAsync(HttpMethod.Delete, w.Base)).StatusCode); // as an old client sends it
        Assert.Equal((ConnectionStatuses.Disconnected, 0), await StateAsync(w));
        var other = await JiraKit.ConnectedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await other.Admin.PostAsync($"{other.Base}/disconnect", new { })).StatusCode);
        Assert.Equal((ConnectionStatuses.Disconnected, 0), await StateAsync(other));
    }
}
