using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>ADR-0014 Part A: the language an account has (registered in, or picked in the menu) decides the language of the mails to it; before one is known the Accept-Language header does.</summary>
[Collection(ApiGroup.Name)]
public sealed class AccountLanguageTests(ApiFactory api)
{
    private static TestUser WithHeader(TestUser user, string? header)
    {
        user.Http.DefaultRequestHeaders.Remove("Accept-Language");
        if (header is not null) user.Http.DefaultRequestHeaders.Add("Accept-Language", header);
        return user;
    }

    private async Task<string?> StoredAsync(string email)
    {
        await using var db = api.Db();
        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Language).SingleAsync();
    }

    [Theory]
    [InlineData("de-AT,de;q=0.9", "de", "Bestätigen")]
    [InlineData("tr-TR,tr;q=0.9", "tr", "doğrulayın")]
    [InlineData("fr-FR", "en", "Confirm")]
    public async Task A_registration_keeps_the_language_it_was_made_in_and_the_verification_mail_is_in_it(string header, string language, string subjectPart)
    {
        var client = WithHeader(api.NewClient(), header);
        var email = Emails.New("lang");

        (await client.PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "L" })).EnsureSuccessStatusCode();

        Assert.Equal(language, await StoredAsync(email));
        Assert.Contains(subjectPart, api.Mail.Sent.Reverse().First(m => m.To == email).Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_reset_follows_the_language_of_the_account_not_the_header_of_whoever_asks()
    {
        var owner = WithHeader(api.NewClient(), "de");
        var email = Emails.New("reset-lang");
        (await owner.PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "L" })).EnsureSuccessStatusCode();

        var someone = WithHeader(api.NewClient(), "tr");
        (await someone.PostAsync("/api/auth/password/forgot", new { email })).EnsureSuccessStatusCode();

        Assert.Contains("Passwort", api.Mail.Sent.Reverse().First(m => m.To == email).Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_without_a_language_is_mailed_in_the_language_of_the_header()
    {
        var user = await api.NewClient().SignedUpAsync("nolang");
        await using (var db = api.Db())
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, (string?)null));
        }

        (await WithHeader(api.NewClient(), "tr").PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();

        Assert.Contains("sıfırlayın", api.Mail.Sent.Reverse().First(m => m.To == user.Email).Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_menu_choice_is_kept_on_the_account_and_shown_by_me()
    {
        var user = await api.NewClient().SignedUpAsync("menu");

        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Put, "/api/me/language", new { language = "tr" })).StatusCode);

        Assert.Equal("tr", await StoredAsync(user.Email));
        Assert.Equal("tr", (await user.GetJsonAsync("/api/me")).GetProperty("language").GetString());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("TR")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Only_a_language_we_have_is_accepted(string? language)
    {
        var user = await api.NewClient().SignedUpAsync("badlang");
        var before = await StoredAsync(user.Email);

        var response = await user.SendAsync(HttpMethod.Put, "/api/me/language", new { language });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("language", out _));
        Assert.Equal(before, await StoredAsync(user.Email));
    }

    [Fact]
    public async Task Setting_the_language_needs_a_signed_in_user_and_the_database_refuses_a_language_outside_the_three()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().SendAsync(HttpMethod.Put, "/api/me/language", new { language = "de" })).StatusCode);
        var user = await api.NewClient().SignedUpAsync("dbcheck");
        await using var db = api.Db();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, "fr"));
        });
    }

    [Fact]
    public async Task An_invitation_to_an_existing_account_is_in_the_language_of_that_account()
    {
        var owner = await api.NewClient().SignedUpAsync("inviter");
        var guest = WithHeader(api.NewClient(), "de");
        var email = Emails.New("invited-lang");
        (await guest.PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "G" })).EnsureSuccessStatusCode();

        (await WithHeader(owner, "tr").PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email, role = "viewer" })).EnsureSuccessStatusCode();

        Assert.Contains("Arbeitsbereich", api.Mail.Sent.Reverse().First(m => m.To == email).Subject, StringComparison.Ordinal);
    }
}
