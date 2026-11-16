using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;
using Podium.Api.Text;

namespace Podium.Api.Tests;

/// <summary>
/// F-180: an address is found by its identity key (trimmed, lower-cased, never accent-folded), so a look-alike address ("josé" for "jose",
/// a dotless i for an i) is another mailbox: it signs in to nothing, gets no reset mail and cannot take an invitation sent to the other one.
/// The same address in other letter case is still the same account.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class EmailIdentityTests(ApiFactory api)
{
    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.Ordinal));

    /// <summary>The look-alike of a test address: one letter of its tag accented or dotless.</summary>
    private static string LookAlike(string email, string tag, string letter, string lookAlike) =>
        email.Replace("+" + tag, "+" + tag.Replace(letter, lookAlike, StringComparison.Ordinal), StringComparison.Ordinal);

    [Fact]
    public void The_key_keeps_accents_and_dotless_i_and_folds_only_case_and_spaces()
    {
        Assert.Equal("jose@gmail.com", EmailIdentity.Key("  JOSE@gmail.com "));
        Assert.NotEqual(EmailIdentity.Key("jose@gmail.com"), EmailIdentity.Key("josé@gmail.com"));
        Assert.NotEqual(EmailIdentity.Key("mail@gmail.com"), EmailIdentity.Key("maıl@gmail.com"));
        Assert.Equal(SearchText.Email("jose@gmail.com"), SearchText.Email("josé@gmail.com"));
        Assert.Null(EmailIdentity.Key("  "));
    }

    [Theory]
    [InlineData("jose", "o", "ó")]
    [InlineData("mail", "i", "ı")]
    public async Task A_look_alike_address_signs_in_to_nothing_and_the_address_in_any_case_still_does(string tag, string letter, string lookAlike)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        var other = LookAlike(user.Email, tag, letter, lookAlike);
        Assert.NotEqual(user.Email, other);

        var client = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/login", new { email = other, password = TestUser.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/auth/login", new { email = user.Email.ToUpperInvariant(), password = TestUser.Password })).StatusCode);
    }

    [Fact]
    public async Task Forgot_for_a_look_alike_address_mails_nothing()
    {
        var user = await api.NewClient().SignedUpAsync("forgotlook");
        var other = LookAlike(user.Email, "forgotlook", "o", "ó");
        var mails = api.Mail.Sent.Count;

        Assert.Equal(HttpStatusCode.Accepted, (await api.NewClient().PostAsync("/api/auth/password/forgot", new { email = other })).StatusCode);

        Assert.Equal(mails, api.Mail.Sent.Count);
    }

    [Fact]
    public async Task A_look_alike_registration_gets_the_answer_of_a_registered_address_and_never_a_500()
    {
        var user = await api.NewClient().SignedUpAsync("reglook");
        var other = LookAlike(user.Email, "reglook", "e", "é");

        var response = await api.NewClient().PostAsync("/api/auth/register", new { email = other, password = TestUser.Password, displayName = "Other" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task An_invitation_to_a_look_alike_address_cannot_be_accepted_by_the_other_account()
    {
        var owner = await api.NewClient().SignedUpAsync("invlook-owner");
        var invitee = await api.NewClient().SignedUpAsync("invlook");
        var other = LookAlike(invitee.Email, "invlook", "i", "ı");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = other, role = "member" })).EnsureSuccessStatusCode();

        var response = await invitee.PostAsync("/api/invitations/accept", new { token = TokenTo(other) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invitation_other_address",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("token")[0].GetString());
    }

    [Fact]
    public async Task An_invitation_to_the_address_in_other_letter_case_is_accepted()
    {
        var owner = await api.NewClient().SignedUpAsync("invcase-owner");
        var invitee = await api.NewClient().SignedUpAsync("invcase");
        var upper = invitee.Email.ToUpperInvariant();
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = upper, role = "member" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync("/api/invitations/accept", new { token = TokenTo(upper) })).StatusCode);
        await using var db = api.Db();
        Assert.Equal(upper, await db.WorkspaceInvitations.Where(i => i.WorkspaceId == owner.WorkspaceId).Select(i => i.Email).SingleAsync());
    }

    [Fact]
    public async Task A_member_or_an_open_invitation_blocks_only_its_own_address_not_a_look_alike()
    {
        var owner = await api.NewClient().SignedUpAsync("duplook-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "duplook");
        var memberLookAlike = LookAlike(member.Email, "duplook", "u", "ú");
        var invited = Emails.New("pendlook");
        var invitedLookAlike = LookAlike(invited, "pendlook", "e", "é");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invited, role = "member" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created,
            (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = memberLookAlike, role = "member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitedLookAlike, role = "member" })).StatusCode);
        Assert.Equal(1, MailsTo(invitedLookAlike));
    }

    /// <summary>The token of the newest mail to exactly this address (the factory's helper ignores case only, so a look-alike is another address).</summary>
    private string TokenTo(string address)
    {
        var mail = api.Mail.Sent.Reverse().First(m => string.Equals(m.To, address, StringComparison.Ordinal));
        var at = mail.Body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = mail.Body.IndexOfAny(['\n', ' '], at);
        return Uri.UnescapeDataString(mail.Body[at..(end < 0 ? mail.Body.Length : end)]);
    }
}
