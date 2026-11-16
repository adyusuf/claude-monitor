using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Text;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-059 part 2: the invitation list shows the address as the inviter typed it (trimmed); an invitation from before the column shows the normalised one.</summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationEmailDisplayTests(ApiFactory api)
{
    [Fact]
    public async Task The_list_shows_the_address_as_typed_and_the_row_keeps_it_beside_the_normalised_key()
    {
        var owner = await api.NewClient().SignedUpAsync("inv-typed-owner");
        var typed = Emails.New("Inv-Typed").Replace("aipod", "Aipod", StringComparison.Ordinal).Replace("@gmail", "@Gmail", StringComparison.Ordinal);
        Assert.NotEqual(SearchText.Email(typed), typed);

        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = "  " + typed + "  ", role = "member" })).EnsureSuccessStatusCode();

        var listed = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations");
        Assert.Equal(typed, listed[0].GetProperty("email").GetString());
        await using var db = api.Db();
        var row = await db.WorkspaceInvitations.AsNoTracking().SingleAsync(i => i.WorkspaceId == owner.WorkspaceId);
        Assert.Equal(typed, row.Email);
        Assert.Equal(SearchText.Email(typed), row.EmailNormalized);
    }

    [Fact]
    public async Task An_invitation_from_before_the_column_shows_the_normalised_address()
    {
        var owner = await api.NewClient().SignedUpAsync("inv-old-owner");
        var old = Emails.New("inv-old");
        await using (var db = api.Db())
        {
            var now = api.Clock.GetUtcNow();
            db.WorkspaceInvitations.Add(new WorkspaceInvitation
            {
                WorkspaceId = owner.WorkspaceId, EmailNormalized = SearchText.Email(old), Email = null, Role = Roles.Member,
                TokenHash = Secrets.Hash(Secrets.NewToken()), InvitedBy = owner.Id, CreatedAt = now, ExpiresAt = now.AddDays(7),
            });
            await db.SaveChangesAsync();
        }

        var listed = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations");

        Assert.Equal(SearchText.Email(old), listed[0].GetProperty("email").GetString());
    }
}
