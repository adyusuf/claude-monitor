using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;
using Podium.Api.Text;

namespace Podium.Api.Tests;

/// <summary>
/// Account deletion and the invitations addressed to the deleted person: the pending ones are revoked and the address as the inviter
/// typed it leaves every invitation to that address; an invitation to anyone else keeps its address.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccountDeletionInvitationTests(ApiFactory api)
{
    [Fact]
    public async Task Only_the_invitations_to_the_deleted_mailbox_are_revoked_and_lose_the_typed_address_a_look_alike_keeps_its_own()
    {
        var owner = await api.NewClient().SignedUpAsync("delinv-owner");
        var invitee = await api.NewClient().SignedUpAsync("delinv");
        // The inviter typed a capitalised spelling of the invitee's address: the same mailbox. An accented look-alike folds to the same normalised
        // address but is another mailbox (F-180): its invitation is not the deleted person's.
        var typed = invitee.Email.Replace("+delinv", "+Delinv", StringComparison.Ordinal);
        var lookAlike = invitee.Email.Replace("+delinv", "+délinv", StringComparison.Ordinal);
        var unrelated = Emails.New("Délinv-unrelated");
        Assert.NotEqual(invitee.Email, typed);
        Assert.Equal(SearchText.Email(invitee.Email), SearchText.Email(lookAlike));
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = typed, role = "member" })).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = lookAlike, role = "member" })).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = unrelated, role = "member" })).EnsureSuccessStatusCode();
        await using (var before = api.Db())
        {
            var addresses = await before.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == owner.WorkspaceId).Select(i => i.Email).ToListAsync();
            Assert.Contains(typed, addresses);
            Assert.Contains(lookAlike, addresses);
            Assert.Contains(unrelated, addresses);
        }

        (await invitee.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == owner.WorkspaceId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, i => i.Email == typed);
        var deleted = Assert.Single(rows, i => i.Email is null);
        Assert.Equal(SearchText.Email(typed), deleted.EmailNormalized);
        Assert.NotNull(deleted.RevokedAt);
        foreach (var address in new[] { lookAlike, unrelated })
        {
            var kept = Assert.Single(rows, i => i.Email == address);
            Assert.Null(kept.RevokedAt);
        }
    }
}
