using ClaudeMonitor.Api.Mail;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>A mail server that is down while <see cref="Down"/> is set and otherwise hands the message to the factory's capturing mailer.</summary>
public sealed class FlakyMailer(FakeMailer inner) : IMailer
{
    public bool Down { get; set; }

    public Task SendAsync(MailMessageData message, CancellationToken ct) =>
        Down ? throw new InvalidOperationException("the mail server is down") : inner.SendAsync(message, ct);
}
