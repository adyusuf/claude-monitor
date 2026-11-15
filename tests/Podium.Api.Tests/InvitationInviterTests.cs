using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// An invitation speaks for its inviter (F-032): accepting needs the inviter to be still able to give it, an active admin or owner of at
/// least the invited rank. Removing or demoting the inviter voids their open invitations.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationInviterTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Admin, TestUser Invitee, string Token)> InvitedByAdminAsync(string tag, string role)
    {
        var owner = await api.NewClient().SignedUpAsync($"{tag}-owner");
        var admin = await RemoteKit.MemberAsync(api, owner, $"{tag}-admin", "admin");
        var invitee = await api.NewClient().SignedUpAsync($"{tag}-invitee");
        (await admin.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitee.Email, role })).EnsureSuccessStatusCode();
        return (owner, admin, invitee, api.Mail.TokenFor(invitee.Email));
    }

    private async Task AssertRefusedAsync(TestUser owner, TestUser invitee, string token)
    {
        var response = await invitee.PostAsync("/api/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_token", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("token")[0].GetString());
        await using var db = api.Db();
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == invitee.Id));
    }

    [Fact]
    public async Task An_invitation_of_an_inviter_who_was_removed_cannot_be_accepted()
    {
        var (owner, admin, invitee, token) = await InvitedByAdminAsync("inv-gone", "admin");
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{admin.Id}")).EnsureSuccessStatusCode();

        await AssertRefusedAsync(owner, invitee, token);
    }

    [Fact]
    public async Task An_invitation_of_an_inviter_who_was_demoted_below_admin_cannot_be_accepted()
    {
        var (owner, admin, invitee, token) = await InvitedByAdminAsync("inv-demoted", "member");
        (await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{admin.Id}", new { role = "member" })).EnsureSuccessStatusCode();

        await AssertRefusedAsync(owner, invitee, token);
    }

    [Fact]
    public async Task An_invitation_of_an_inviter_who_is_still_an_admin_is_accepted()
    {
        var (owner, _, invitee, token) = await InvitedByAdminAsync("inv-stays", "admin");

        var response = await invitee.PostAsync("/api/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = api.Db();
        Assert.Equal("admin", await db.WorkspaceMembers.Where(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == invitee.Id).Select(m => m.Role).SingleAsync());
    }
}
