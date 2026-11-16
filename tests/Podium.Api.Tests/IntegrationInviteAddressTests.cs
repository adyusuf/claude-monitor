using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Api.Text;

namespace Podium.Api.Tests;

/// <summary>
/// F-180 for the invitations the Jira people sync makes: the address as Jira shows it (trimmed) is kept on the row, so the mail, a resend and
/// the list use that mailbox and not its folded look-alike; an open invitation is judged by the identity key; a row from before the typed
/// address cannot be sent again (its mailbox is not known).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationInviteAddressTests(ApiFactory api)
{
    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.Ordinal));

    private string TokenTo(string address)
    {
        var mail = api.Mail.Sent.Reverse().First(m => string.Equals(m.To, address, StringComparison.Ordinal) && m.Body.Contains("token=", StringComparison.Ordinal));
        var at = mail.Body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = mail.Body.IndexOfAny(['\n', ' '], at);
        return Uri.UnescapeDataString(mail.Body[at..(end < 0 ? mail.Body.Length : end)]);
    }

    private async Task<JiraWorld> InvitingWorldAsync()
    {
        var w = await JiraKit.ConnectedAsync(api);
        (await w.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).EnsureSuccessStatusCode();
        return w;
    }

    private async Task<List<WorkspaceInvitation>> InvitationsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == w.WorkspaceId).ToListAsync();
    }

    [Fact]
    public async Task An_auto_invitation_keeps_the_address_as_typed_and_the_resend_mails_that_mailbox_which_a_look_alike_account_cannot_take()
    {
        var w = await InvitingWorldAsync();
        var plain = await api.NewClient().SignedUpAsync("autolook");
        var accented = "  " + plain.Email.Replace("+autolook", "+autolóok", StringComparison.Ordinal) + " ";
        Assert.Equal(SearchText.Email(plain.Email), SearchText.Email(accented));
        w.Site.Space("ACC").People.Add(new FakePerson("p-acc", "Accented", accented));

        await JiraKit.ImportAsync(w, "ACC");

        var typed = accented.Trim();
        var invitation = Assert.Single(await InvitationsAsync(w));
        Assert.Equal(typed, invitation.Email);
        Assert.Equal(1, MailsTo(typed));
        Assert.Equal(typed, (await w.Admin.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/invitations"))[0].GetProperty("email").GetString());
        var resent = await w.Admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/invitations/{invitation.Id}/resend");
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal(2, MailsTo(typed));
        var response = await plain.PostAsync("/api/invitations/accept", new { token = TokenTo(typed) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invitation_other_address", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("token")[0].GetString());
    }

    [Fact]
    public async Task An_open_invitation_to_a_look_alike_does_not_stop_the_auto_invitation_and_an_admin_invite_of_the_same_address_is_pending()
    {
        var w = await InvitingWorldAsync();
        var plain = Emails.New("autokey");
        var accented = plain.Replace("+autokey", "+autókey", StringComparison.Ordinal);
        (await w.Admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/invitations", new { email = plain, role = "member" })).EnsureSuccessStatusCode();
        w.Site.Space("KEY").People.Add(new FakePerson("p-key", "Key", accented));

        await JiraKit.ImportAsync(w, "KEY");

        Assert.Equal(1, MailsTo(accented));
        var again = await w.Admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/invitations", new { email = accented, role = "member" });
        await JiraWriteKit.AssertRefusedAsync(again, HttpStatusCode.Conflict, "invitation_pending");
    }

    [Fact]
    public async Task A_resend_of_a_row_from_before_the_typed_address_is_refused_as_unknown_and_changes_nothing()
    {
        var w = await JiraKit.ConnectedAsync(api);
        // Only the folded address was saved: whether the person's mailbox had an accent or a dotless i cannot be known, so no mail goes to the folded one.
        var legacy = await LegacyAsync(w, Emails.New("legacy").ToLowerInvariant());
        var sentBefore = api.Mail.Sent.Count;

        var refused = await w.Admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/invitations/{legacy.Id}/resend");

        await JiraWriteKit.AssertRefusedAsync(refused, HttpStatusCode.Conflict, "invitation_address_unknown");
        Assert.Equal(sentBefore, api.Mail.Sent.Count);
        await using var db = api.Db();
        Assert.Equal(legacy.TokenHash, (await db.WorkspaceInvitations.AsNoTracking().SingleAsync(i => i.Id == legacy.Id)).TokenHash);
    }

    /// <summary>An open invitation as an older release saved it: the folded address only.</summary>
    private async Task<WorkspaceInvitation> LegacyAsync(JiraWorld w, string address)
    {
        var now = api.Clock.GetUtcNow();
        var row = new WorkspaceInvitation
        {
            WorkspaceId = w.WorkspaceId, EmailNormalized = SearchText.Email(address), Email = null, Role = Roles.Viewer, TokenHash = Secrets.Hash(Secrets.NewToken()),
            InvitedBy = w.Admin.Id, CreatedAt = now, ExpiresAt = now.AddDays(7),
        };
        await using var db = api.Db();
        db.WorkspaceInvitations.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}
