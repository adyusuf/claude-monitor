using System.Net;
using Podium.Api.Config;
using Podium.Api.Mail;
using Podium.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Podium.Api.Tests;

/// <summary>The SMTP mailer uses the settings in force at each send (ADR-0017), checked against mail servers on the loopback address that take one message each.</summary>
[Collection(ApiGroup.Name)]
public sealed class SmtpMailerSettingsTests(ApiFactory api) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private SmtpMailer Mailer() => new(api.Services.GetRequiredService<IntegrationSettings>(), NullLogger<SmtpMailer>.Instance);

    private static async Task SendAsync(SmtpMailer mailer, string to)
    {
        using var timeout = new CancellationTokenSource(Wait);
        await mailer.SendAsync(MailTemplates.Build("en", MailTemplates.Test, to), timeout.Token);
    }

    [Fact]
    public async Task Each_send_uses_the_host_port_and_sender_in_force_at_that_moment()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sm-switch");
        await using var first = new FakeSmtp();
        await using var second = new FakeSmtp();
        var from = Emails.New("sm-from");
        var to = Emails.New("sm-to");
        var mailer = Mailer(); // one mailer for all sends: it must not remember the settings
        (await ServerAdminKit.PutAsync(admin, "smtp", new { host = IPAddress.Loopback.ToString(), port = first.PortText, from, tls = "false" })).EnsureSuccessStatusCode();

        await SendAsync(mailer, to);

        var one = await first.NextAsync();
        Assert.Equal(from, one.From);
        Assert.Equal([to], one.To);
        Assert.Contains("Subject: " + MailTemplates.Build("en", MailTemplates.Test, to).Subject, one.Data, StringComparison.Ordinal);
        Assert.Equal((null, null), (one.User, one.Password)); // no user is saved: no sign-in is attempted

        (await ServerAdminKit.PutAsync(admin, "smtp", new { port = second.PortText })).EnsureSuccessStatusCode();
        await SendAsync(mailer, to);

        var two = await second.NextAsync();
        Assert.Equal(from, two.From);
        Assert.Equal([to], two.To);
        Assert.False(first.HasMail); // the first server was not used again
        Assert.False(second.HasMail);
    }

    [Fact]
    public async Task The_saved_user_and_the_sealed_password_are_what_the_mail_server_is_signed_in_with_and_a_removed_user_stops_the_sign_in()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sm-auth");
        await using var server = new FakeSmtp(auth: true);
        var from = Emails.New("sm-auth-from");
        var to = Emails.New("sm-auth-to");
        var mailer = Mailer();
        (await ServerAdminKit.PutAsync(admin, "smtp", new { host = IPAddress.Loopback.ToString(), port = server.PortText, from, tls = "false", user = "mailer-account", password = "p@ssword-91" })).EnsureSuccessStatusCode();

        await SendAsync(mailer, to);

        var signedIn = await server.NextAsync();
        Assert.Equal(("mailer-account", "p@ssword-91"), (signedIn.User, signedIn.Password));

        (await ServerAdminKit.PutAsync(admin, "smtp", new { user = "" })).EnsureSuccessStatusCode();
        await SendAsync(mailer, to);

        var anonymous = await server.NextAsync();
        Assert.Equal((null, null), (anonymous.User, anonymous.Password));
        Assert.Equal([to], anonymous.To);
    }

    [Fact]
    public async Task A_test_mail_from_the_page_goes_through_the_saved_server_when_the_real_mailer_is_in_use()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sm-page");
        await using var server = new FakeSmtp();
        var from = Emails.New("sm-page-from");
        (await ServerAdminKit.PutAsync(admin, "smtp", new { host = IPAddress.Loopback.ToString(), port = server.PortText, from, tls = "false" })).EnsureSuccessStatusCode();
        var recipient = admin.Email;

        await SendAsync(Mailer(), recipient);

        var mail = await server.NextAsync();
        Assert.Equal([recipient], mail.To);
        Assert.Equal(from, mail.From);
    }
}
