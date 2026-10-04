using System.Net;
using System.Net.Mail;
using ClaudeMonitor.Api.Config;

namespace ClaudeMonitor.Api.Mail;

public sealed record MailMessageData(string To, string Subject, string Body);

public interface IMailer
{
    Task SendAsync(MailMessageData message, CancellationToken ct);
}

/// <summary>Plain SMTP with STARTTLS (Gmail / Google Workspace in production, Mailpit in development).</summary>
public sealed class SmtpMailer(ApiConfig config, ILogger<SmtpMailer> log) : IMailer
{
    public async Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = config.Smtp;
        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.StartTls };
        if (!string.IsNullOrEmpty(smtp.User))
        {
            client.Credentials = new NetworkCredential(smtp.User, smtp.Password);
        }

        using var mail = new MailMessage(smtp.From, message.To, message.Subject, message.Body);
        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (SmtpException e)
        {
            // The recipient is personal data: only the subject is logged.
            log.LogError(e, "sending mail failed: {Subject}", message.Subject);
            throw;
        }
    }
}

/// <summary>The mails the API sends, in the user's language (English primary, Turkish second).</summary>
public static class MailTemplates
{
    private static readonly Dictionary<string, Dictionary<string, (string Subject, string Body)>> Text = new()
    {
        ["en"] = new()
        {
            ["verify"] = ("Confirm your e-mail address",
                "Open this link to confirm your address for Claude Monitor:\n\n{0}\n\nThe link works for 24 hours. If you did not sign up, ignore this mail."),
            ["reset"] = ("Reset your password",
                "Open this link to choose a new password:\n\n{0}\n\nThe link works for 24 hours. If you did not ask for it, ignore this mail."),
            ["invite"] = ("You are invited to a workspace",
                "{1} invited you to the workspace \"{2}\" on Claude Monitor. Open this link to join:\n\n{0}\n\nThe invitation works for 7 days."),
        },
        ["tr"] = new()
        {
            ["verify"] = ("E-posta adresinizi doğrulayın",
                "Claude Monitor adresinizi doğrulamak için bu bağlantıyı açın:\n\n{0}\n\nBağlantı 24 saat geçerlidir. Kaydolmadıysanız bu e-postayı yok sayın."),
            ["reset"] = ("Parolanızı sıfırlayın",
                "Yeni bir parola seçmek için bu bağlantıyı açın:\n\n{0}\n\nBağlantı 24 saat geçerlidir. Siz istemediyseniz bu e-postayı yok sayın."),
            ["invite"] = ("Bir çalışma alanına davet edildiniz",
                "{1} sizi Claude Monitor'daki \"{2}\" çalışma alanına davet etti. Katılmak için bu bağlantıyı açın:\n\n{0}\n\nDavet 7 gün geçerlidir."),
        },
    };

    public static string Language(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var header = http.Request.Headers.AcceptLanguage.ToString();
        return header.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
    }

    public static MailMessageData Build(string language, string key, string to, params object[] args)
    {
        var table = Text.TryGetValue(language, out var t) ? t : Text["en"];
        var (subject, body) = table[key];
        return new MailMessageData(to, subject, string.Format(System.Globalization.CultureInfo.InvariantCulture, body, args));
    }
}
