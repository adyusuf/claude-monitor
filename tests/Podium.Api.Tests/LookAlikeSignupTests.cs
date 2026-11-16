using System.Net;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;
using Podium.Api.Text;

namespace Podium.Api.Tests;

/// <summary>
/// F-180 end to end: an address and its accent or dotless-i look-alike (which the search fold treats as one) are two mailboxes, so they register as
/// two accounts that each sign in only with their own address; registering the identical address again keeps the non-enumerating 202.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class LookAlikeSignupTests(ApiFactory api)
{
    private const string FirstPassword = "first password long";
    private const string SecondPassword = "second password long";

    private static Task<HttpResponseMessage> RegisterAsync(TestUser client, string email, string password, string name) =>
        client.PostAsync("/api/auth/register", new { email, password, displayName = name });

    private static Task<HttpResponseMessage> LoginAsync(string email, string password, TestUser client) =>
        client.PostAsync("/api/auth/login", new { email, password });

    [Theory]
    [InlineData("jose", "o", "ó")]
    [InlineData("mail", "i", "ı")]
    public async Task An_address_and_its_look_alike_register_as_two_accounts_that_each_sign_in_only_with_their_own_address(string tag, string letter, string lookAlike)
    {
        var first = Emails.New(tag);
        var second = first.Replace("+" + tag, "+" + tag.Replace(letter, lookAlike, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.Equal(SearchText.Email(first), SearchText.Email(second));

        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(api.NewClient(), first, FirstPassword, "First")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(api.NewClient(), second, SecondPassword, "Second")).StatusCode);
        foreach (var email in new[] { first, second })
        {
            (await api.NewClient().PostAsync("/api/auth/verify-email", new { token = TokenTo(email) })).EnsureSuccessStatusCode();
        }

        var client = api.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync(first, FirstPassword, client)).StatusCode);
        var firstMe = await client.GetJsonAsync("/api/me");
        var other = api.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync(second, SecondPassword, other)).StatusCode);
        var secondMe = await other.GetJsonAsync("/api/me");
        Assert.NotEqual(firstMe.GetProperty("id").GetGuid(), secondMe.GetProperty("id").GetGuid());
        Assert.Equal(("First", "Second"), (firstMe.GetProperty("displayName").GetString(), secondMe.GetProperty("displayName").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(first, SecondPassword, api.NewClient())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(second, FirstPassword, api.NewClient())).StatusCode);
        await using var db = api.Db();
        Assert.Equal(2, await db.Users.CountAsync(u => u.EmailNormalized == SearchText.Email(first)));
    }

    [Fact]
    public async Task Registering_the_identical_address_again_still_answers_202_and_creates_no_third_account()
    {
        var first = Emails.New("josé");
        var second = first.Replace("josé", "jose", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(api.NewClient(), first, FirstPassword, "First")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(api.NewClient(), second, SecondPassword, "Second")).StatusCode);
        foreach (var email in new[] { first, second })
        {
            (await api.NewClient().PostAsync("/api/auth/verify-email", new { token = TokenTo(email) })).EnsureSuccessStatusCode();
        }

        var again = await RegisterAsync(api.NewClient(), second, "another password long", "Again");
        var againFirst = await RegisterAsync(api.NewClient(), first, "another password long", "Again");

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, againFirst.StatusCode);
        await using var db = api.Db();
        Assert.Equal(2, await db.Users.CountAsync(u => u.EmailNormalized == SearchText.Email(first)));
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync(second, SecondPassword, api.NewClient())).StatusCode);
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
