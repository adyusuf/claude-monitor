using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// The mailed links run out: 24 hours for the e-mail verification and the password reset, 7 days for a workspace invitation
/// (ApiConfig.EmailTokenLifetime, InvitationLifetime). Each is tried one second before it runs out and one second after.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class TokenExpiryTests(ApiFactory api)
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EmailLink = TimeSpan.FromHours(24);
    private static readonly TimeSpan Invitation = TimeSpan.FromDays(7);
    private const string NewPassword = "another horse battery";

    private async Task<(TestUser Client, string Email, string Token)> RegisteredAsync(string tag)
    {
        var client = api.NewClient();
        var email = Emails.New(tag);
        (await client.PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "Test Person" })).EnsureSuccessStatusCode();
        return (client, email, api.Mail.TokenFor(email));
    }

    private async Task<string> ResetTokenAsync(string email)
    {
        var response = await api.NewClient().PostAsync("/api/auth/password/forgot", new { email });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return api.Mail.TokenFor(email);
    }

    private async Task<(TestUser Owner, TestUser Invitee, string Token)> InvitedAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("exp-owner");
        var invitee = await api.NewClient().SignedUpAsync("exp-invitee");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitee.Email })).EnsureSuccessStatusCode();
        return (owner, invitee, api.Mail.TokenFor(invitee.Email));
    }

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("invalid_token", errors.GetProperty("token")[0].GetString());
    }

    [Fact]
    public async Task A_verification_link_still_works_one_second_before_24_hours()
    {
        var (client, email, token) = await RegisteredAsync("exp-verify-ok");
        api.Clock.Advance(EmailLink - Second);

        var response = await client.PostAsync("/api/auth/verify-email", new { token });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/login", new { email, password = TestUser.Password })).StatusCode);
    }

    [Fact]
    public async Task A_verification_link_is_refused_one_second_after_24_hours_and_the_address_stays_unverified()
    {
        var (client, email, token) = await RegisteredAsync("exp-verify-late");
        api.Clock.Advance(EmailLink + Second);

        var response = await client.PostAsync("/api/auth/verify-email", new { token });

        await AssertInvalidTokenAsync(response);
        var login = await client.PostAsync("/api/auth/login", new { email, password = TestUser.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("email_not_verified", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_reset_link_still_works_one_second_before_24_hours()
    {
        var user = await api.NewClient().SignedUpAsync("exp-reset-ok");
        var token = await ResetTokenAsync(user.Email);
        api.Clock.Advance(EmailLink - Second);

        var response = await api.NewClient().PostAsync("/api/auth/password/reset", new { token, password = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.NewClient().PostAsync("/api/auth/login", new { email = user.Email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task A_reset_link_is_refused_one_second_after_24_hours_and_the_old_password_still_works()
    {
        var user = await api.NewClient().SignedUpAsync("exp-reset-late");
        var token = await ResetTokenAsync(user.Email);
        api.Clock.Advance(EmailLink + Second);

        var response = await api.NewClient().PostAsync("/api/auth/password/reset", new { token, password = NewPassword });

        await AssertInvalidTokenAsync(response);
        Assert.Equal(HttpStatusCode.NoContent, (await api.NewClient().PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().PostAsync("/api/auth/login", new { email = user.Email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task An_invitation_can_still_be_accepted_one_second_before_7_days()
    {
        var (owner, invitee, token) = await InvitedAsync();
        api.Clock.Advance(Invitation - Second);

        var response = await invitee.PostAsync("/api/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(owner.WorkspaceId, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspaceId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await invitee.Http.GetAsync($"/api/workspaces/{owner.WorkspaceId}")).StatusCode);
    }

    [Fact]
    public async Task An_invitation_is_refused_one_second_after_7_days_and_makes_no_member()
    {
        var (owner, invitee, token) = await InvitedAsync();
        api.Clock.Advance(Invitation + Second);

        var response = await invitee.PostAsync("/api/invitations/accept", new { token });

        await AssertInvalidTokenAsync(response);
        Assert.Equal(HttpStatusCode.NotFound, (await invitee.Http.GetAsync($"/api/workspaces/{owner.WorkspaceId}")).StatusCode);
        await using var db = api.Db();
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == invitee.Id));
    }
}
