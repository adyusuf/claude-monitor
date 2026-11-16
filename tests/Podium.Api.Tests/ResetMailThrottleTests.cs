using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Config;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// At most one reset mail per address per window (F-175, ApiConfig.ResetMailEvery): a second Forgot inside it answers 202 as always but
/// mails nothing and leaves the first link working; after the window a Forgot mails a new link again.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ResetMailThrottleTests(ApiFactory api)
{
    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task A_second_forgot_inside_the_window_mails_nothing_and_the_first_link_still_works()
    {
        var user = await api.NewClient().SignedUpAsync("throttle-in");
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var first = api.Mail.TokenFor(user.Email);
        var mails = MailsTo(user.Email);
        api.Clock.Advance(TimeSpan.FromMinutes(4));

        var again = await client.PostAsync("/api/auth/password/forgot", new { email = user.Email.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(mails, MailsTo(user.Email));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/auth/password/reset", new { token = first, password = "a brand new password" })).StatusCode);
    }

    [Fact]
    public async Task The_window_is_five_minutes_one_second_short_mails_nothing_and_five_minutes_mails_again()
    {
        // F-175 pin: the window is exactly five minutes (ApiConfig.ResetMailEvery, not set from the environment).
        Assert.Equal(TimeSpan.FromMinutes(5), api.Services.GetRequiredService<ApiConfig>().ResetMailEvery);
        var user = await api.NewClient().SignedUpAsync("throttle-edge");
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var mails = MailsTo(user.Email);

        api.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        Assert.Equal(mails, MailsTo(user.Email));

        api.Clock.Advance(TimeSpan.FromSeconds(1));
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        Assert.Equal(mails + 1, MailsTo(user.Email));
    }

    [Fact]
    public async Task A_forgot_after_the_window_mails_a_new_link()
    {
        var user = await api.NewClient().SignedUpAsync("throttle-after");
        var client = api.NewClient();
        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();
        var mails = MailsTo(user.Email);
        api.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).EnsureSuccessStatusCode();

        Assert.Equal(mails + 1, MailsTo(user.Email));
    }
}
