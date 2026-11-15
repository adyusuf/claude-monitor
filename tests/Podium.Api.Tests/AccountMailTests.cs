using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Mail;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// Register and Forgot answer the same for a known and an unknown address (F-029): the mail goes out off the request path, so the answer
/// does not wait for the mail server (no SMTP time to measure), and a mail server that fails still answers 202 (logged), never a 500
/// that only a registered address could cause.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccountMailTests(ApiFactory api)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A mail server that holds every send until released, then hands it to the factory's capturing mailer.</summary>
    private sealed class HeldMailer(FakeMailer inner) : IMailer
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(MailMessageData message, CancellationToken ct)
        {
            await Release.Task;
            await inner.SendAsync(message, ct);
        }
    }

    private static object Registration(string email) => new { email, password = TestUser.Password, displayName = "Mail Person" };

    [Fact]
    public async Task With_the_mail_server_down_register_and_forgot_still_answer_202()
    {
        var known = await api.NewClient().SignedUpAsync("mail-down");
        var mailer = new FlakyMailer(api.Mail) { Down = true };
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        var client = JiraKit.ClientOf(api, host);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/auth/register", Registration(Emails.New("mail-down-new")))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/auth/password/forgot", new { email = known.Email })).StatusCode);
    }

    [Fact]
    public async Task Register_and_forgot_answer_before_the_mail_server_does()
    {
        var known = await api.NewClient().SignedUpAsync("mail-held");
        var mailer = new HeldMailer(api.Mail);
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        var client = JiraKit.ClientOf(api, host);
        var fresh = Emails.New("mail-held-new");

        var registered = await client.PostAsync("/api/auth/register", Registration(fresh)).WaitAsync(Patience);
        var forgot = await client.PostAsync("/api/auth/password/forgot", new { email = known.Email }).WaitAsync(Patience);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (registered.StatusCode, forgot.StatusCode));
        Assert.DoesNotContain(api.Mail.Sent, m => m.To == fresh);
        mailer.Release.SetResult();
        using var timeout = new CancellationTokenSource(Patience);
        while (!api.Mail.Sent.Any(m => m.To == fresh)) await Task.Delay(20, timeout.Token);
    }
}
