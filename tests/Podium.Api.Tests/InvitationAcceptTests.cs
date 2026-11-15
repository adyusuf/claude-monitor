using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>What accepting an invitation does beyond the happy path (InvitationEndpoints.Accept): once only, a higher role upgrades, a lower one never downgrades, the address must be verified.</summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationAcceptTests(ApiFactory api)
{
    private async Task<string> InviteAsync(TestUser owner, TestUser invitee, string role)
    {
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitee.Email, role })).EnsureSuccessStatusCode();
        return api.Mail.TokenFor(invitee.Email);
    }

    private static async Task<string> RoleOfAsync(TestUser reader, Guid workspaceId, Guid userId)
    {
        var members = await reader.GetJsonAsync($"/api/workspaces/{workspaceId}/members");
        return members.EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == userId).GetProperty("role").GetString()!;
    }

    [Fact]
    public async Task A_second_accept_of_the_same_token_is_refused_and_adds_no_second_membership_or_audit_row()
    {
        var owner = await api.NewClient().SignedUpAsync("acc-owner");
        var invitee = await api.NewClient().SignedUpAsync("acc-invitee");
        var token = await InviteAsync(owner, invitee, "member");
        (await invitee.PostAsync("/api/invitations/accept", new { token })).EnsureSuccessStatusCode();

        var again = await invitee.PostAsync("/api/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        var errors = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("invalid_token", errors.GetProperty("token")[0].GetString());
        await using var db = api.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.WorkspaceId == owner.WorkspaceId && a.Action == AuditActions.InvitationAccepted));
        Assert.Equal(2, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == owner.WorkspaceId));
    }

    [Fact]
    public async Task An_invitation_to_a_higher_role_upgrades_an_existing_member()
    {
        var owner = await api.NewClient().SignedUpAsync("acc-up-owner");
        var invitee = await api.NewClient().SignedUpAsync("acc-up-invitee");
        (await invitee.PostAsync("/api/invitations/accept", new { token = await InviteAsync(owner, invitee, "member") })).EnsureSuccessStatusCode();

        var response = await invitee.PostAsync("/api/invitations/accept", new { token = await InviteAsync(owner, invitee, "admin") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin", await RoleOfAsync(owner, owner.WorkspaceId, invitee.Id));
    }

    [Fact]
    public async Task An_invitation_to_a_lower_role_never_downgrades_an_existing_member()
    {
        var owner = await api.NewClient().SignedUpAsync("acc-down-owner");
        var invitee = await api.NewClient().SignedUpAsync("acc-down-invitee");
        (await invitee.PostAsync("/api/invitations/accept", new { token = await InviteAsync(owner, invitee, "admin") })).EnsureSuccessStatusCode();
        // The invite endpoint now refuses a lower role for a member (F-048): the open lower invitation is one made before they joined
        // higher (a Jira people invitation, say), written here directly.
        var lower = Secrets.NewToken();
        await using (var db = api.Db())
        {
            var now = api.Clock.GetUtcNow();
            db.WorkspaceInvitations.Add(new Podium.Api.Data.WorkspaceInvitation
            {
                WorkspaceId = owner.WorkspaceId,
                EmailNormalized = Podium.Api.Text.SearchText.Email(invitee.Email),
                Role = "viewer",
                TokenHash = Secrets.Hash(lower),
                InvitedBy = owner.Id,
                CreatedAt = now,
                ExpiresAt = now + TimeSpan.FromDays(7),
            });
            await db.SaveChangesAsync();
        }

        var response = await invitee.PostAsync("/api/invitations/accept", new { token = lower });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin", await RoleOfAsync(owner, owner.WorkspaceId, invitee.Id));
    }

    [Fact]
    public async Task An_account_whose_address_is_not_verified_cannot_accept_and_the_invitation_stays_open()
    {
        var owner = await api.NewClient().SignedUpAsync("acc-unv-owner");
        var invitee = await api.NewClient().SignedUpAsync("acc-unv-invitee");
        var token = await InviteAsync(owner, invitee, "member");
        await using (var db = api.Db())
        {
            await db.Users.Where(u => u.Id == invitee.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, (DateTimeOffset?)null));
        }

        var response = await invitee.PostAsync("/api/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("invitation_other_address", errors.GetProperty("token")[0].GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await invitee.Http.GetAsync($"/api/workspaces/{owner.WorkspaceId}")).StatusCode);
        Assert.Equal(1, (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations")).GetArrayLength());
    }
}
