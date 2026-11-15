using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// An invitation that could change nothing is refused (F-048): an active member already at the invited rank or above is 409
/// already_member, an address with an open invitation is 409 invitation_pending (revoke it to send a new one). A higher role for an existing
/// member, a removed member and an address whose invitation was revoked are still invited. The endpoint also carries the rate limit
/// (RateLimitPolicyTests).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationDuplicateTests(ApiFactory api)
{
    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase));

    private static Task<HttpResponseMessage> InviteAsync(TestUser owner, string email, string role) =>
        owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email, role });

    private async Task AssertConflictAsync(HttpResponseMessage response, string code, string address, int mails)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(mails, MailsTo(address));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task Inviting_an_active_member_to_their_role_or_a_lower_one_is_409(string role)
    {
        var owner = await api.NewClient().SignedUpAsync("dup-member-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "dup-member");
        var mails = MailsTo(member.Email);

        await AssertConflictAsync(await InviteAsync(owner, member.Email.ToUpperInvariant(), role), "already_member", member.Email, mails);
        Assert.Equal(0, (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations")).GetArrayLength());
    }

    [Fact]
    public async Task Inviting_an_active_member_to_a_higher_role_is_still_an_invitation()
    {
        var owner = await api.NewClient().SignedUpAsync("dup-up-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "dup-up", "viewer");

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, member.Email, "member")).StatusCode);
    }

    [Fact]
    public async Task An_address_with_an_open_invitation_is_409_until_that_one_is_revoked()
    {
        var owner = await api.NewClient().SignedUpAsync("dup-pending-owner");
        var email = Emails.New("dup-pending");
        var first = await InviteAsync(owner, email, "member");
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await AssertConflictAsync(await InviteAsync(owner, email, "admin"), "invitation_pending", email, 1);
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/invitations/{id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, email, "admin")).StatusCode);
        Assert.Equal(2, MailsTo(email));
    }

    [Fact]
    public async Task An_expired_invitation_does_not_block_a_new_one()
    {
        var owner = await api.NewClient().SignedUpAsync("dup-expired-owner");
        var email = Emails.New("dup-expired");
        (await InviteAsync(owner, email, "member")).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, email, "member")).StatusCode);
    }

    [Fact]
    public async Task A_removed_member_can_be_invited_again()
    {
        var owner = await api.NewClient().SignedUpAsync("dup-removed-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "dup-removed");
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, member.Email, "member")).StatusCode);
    }
}
