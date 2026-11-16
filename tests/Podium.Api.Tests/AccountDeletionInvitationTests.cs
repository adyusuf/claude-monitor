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
    public async Task The_typed_address_leaves_the_invitations_to_the_deleted_person_and_stays_on_the_others()
    {
        var owner = await api.NewClient().SignedUpAsync("delinv-owner");
        var invitee = await api.NewClient().SignedUpAsync("delinv");
        // The inviter typed a capitalised, accented spelling of the invitee's address: it folds to the invitee's normalised address.
        var typed = invitee.Email.Replace("+delinv", "+Délinv", StringComparison.Ordinal);
        var unrelated = Emails.New("Délinv-unrelated");
        Assert.Equal(SearchText.Email(invitee.Email), SearchText.Email(typed));
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = typed, role = "member" })).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = unrelated, role = "member" })).EnsureSuccessStatusCode();
        await using (var before = api.Db())
        {
            var addresses = await before.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == owner.WorkspaceId).Select(i => i.Email).ToListAsync();
            Assert.Contains(typed, addresses);
            Assert.Contains(unrelated, addresses);
        }

        (await invitee.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == owner.WorkspaceId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, i => i.Email == typed);
        var deleted = Assert.Single(rows, i => i.EmailNormalized == SearchText.Email(typed));
        Assert.Null(deleted.Email);
        Assert.NotNull(deleted.RevokedAt);
        var kept = Assert.Single(rows, i => i.EmailNormalized == SearchText.Email(unrelated));
        Assert.Equal(unrelated, kept.Email);
        Assert.Null(kept.RevokedAt);
    }
}
