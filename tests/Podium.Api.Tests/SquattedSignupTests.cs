using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-031: a second registration of an address whose account is still unverified (possibly someone else's squat) answers 202 as always and
/// mails a reset-password link, never a verification: whoever proves the mailbox sets their own password, and the verification link mailed
/// before no longer confirms the first registrant's password. A verified address still gets nothing (AuthTests).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class SquattedSignupTests(ApiFactory api)
{
    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase));

    private static Task<HttpResponseMessage> RegisterAsync(TestUser client, string email, string password, string name) =>
        client.PostAsync("/api/auth/register", new { email, password, displayName = name });

    [Fact]
    public async Task A_second_registration_of_an_unverified_address_mails_a_reset_link_that_hands_the_account_to_the_mailbox()
    {
        var email = Emails.New("squat");
        var squatter = api.NewClient();
        (await RegisterAsync(squatter, email, "squatter password!", "Squatter")).EnsureSuccessStatusCode();
        var squatterVerify = api.Mail.TokenFor(email);
        var owner = api.NewClient();

        var again = await RegisterAsync(owner, email, "owner password long", "Owner");

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(2, MailsTo(email));
        var mail = api.Mail.Sent.Last(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/reset-password?token=", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/verify-email", mail.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await squatter.PostAsync("/api/auth/verify-email", new { token = squatterVerify })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsync("/api/auth/password/reset", new { token = api.Mail.TokenFor(email), password = "owner password long" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/auth/login", new { email, password = "owner password long" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await squatter.PostAsync("/api/auth/login", new { email, password = "squatter password!" })).StatusCode);
    }

    [Fact]
    public async Task Repeated_registrations_inside_the_reset_window_mail_one_link()
    {
        var email = Emails.New("squat-throttle");
        (await RegisterAsync(api.NewClient(), email, TestUser.Password, "First")).EnsureSuccessStatusCode();
        (await RegisterAsync(api.NewClient(), email, TestUser.Password, "Second")).EnsureSuccessStatusCode();

        (await RegisterAsync(api.NewClient(), email, TestUser.Password, "Third")).EnsureSuccessStatusCode();

        Assert.Equal(2, MailsTo(email));
    }
}
