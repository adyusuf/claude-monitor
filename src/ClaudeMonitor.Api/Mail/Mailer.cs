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

/// <summary>The mails the API sends, in the user's language (English primary, then Turkish and German); the site is called Podium.</summary>
public static class MailTemplates
{
    private static readonly Dictionary<string, Dictionary<string, (string Subject, string Body)>> Text = new()
    {
        ["en"] = new()
        {
            ["verify"] = ("Confirm your e-mail address",
                "Open this link to confirm your address for Podium:\n\n{0}\n\nThe link works for 24 hours. If you did not sign up, ignore this mail."),
            ["reset"] = ("Reset your password",
                "Open this link to choose a new password:\n\n{0}\n\nThe link works for 24 hours. If you did not ask for it, ignore this mail."),
            ["invite"] = ("You are invited to a workspace",
                "{1} invited you to the workspace \"{2}\" on Podium. Open this link to join:\n\n{0}\n\nThe invitation works for 7 days."),
        },
        ["tr"] = new()
        {
            ["verify"] = ("E-posta adresinizi doğrulayın",
                "Podium adresinizi doğrulamak için bu bağlantıyı açın:\n\n{0}\n\nBağlantı 24 saat geçerlidir. Kaydolmadıysanız bu e-postayı yok sayın."),
            ["reset"] = ("Parolanızı sıfırlayın",
                "Yeni bir parola seçmek için bu bağlantıyı açın:\n\n{0}\n\nBağlantı 24 saat geçerlidir. Siz istemediyseniz bu e-postayı yok sayın."),
            ["invite"] = ("Bir çalışma alanına davet edildiniz",
                "{1} sizi Podium'daki \"{2}\" çalışma alanına davet etti. Katılmak için bu bağlantıyı açın:\n\n{0}\n\nDavet 7 gün geçerlidir."),
        },
        ["de"] = new()
        {
            ["verify"] = ("Bestätigen Sie Ihre E-Mail-Adresse",
                "Öffnen Sie diesen Link, um Ihre Adresse für Podium zu bestätigen:\n\n{0}\n\nDer Link ist 24 Stunden gültig. Wenn Sie sich nicht registriert haben, ignorieren Sie diese E-Mail."),
            ["reset"] = ("Setzen Sie Ihr Passwort zurück",
                "Öffnen Sie diesen Link, um ein neues Passwort zu wählen:\n\n{0}\n\nDer Link ist 24 Stunden gültig. Wenn Sie ihn nicht angefordert haben, ignorieren Sie diese E-Mail."),
            ["invite"] = ("Sie wurden zu einem Arbeitsbereich eingeladen",
                "{1} hat Sie zum Arbeitsbereich \"{2}\" auf Podium eingeladen. Öffnen Sie diesen Link, um beizutreten:\n\n{0}\n\nDie Einladung ist 7 Tage gültig."),
        },
    };

    /// <summary>The languages the mails exist in.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "tr", "de"];

    public static string Language(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return LanguageOf(http.Request.Headers.AcceptLanguage.ToString());
    }

    /// <summary>
    /// The best of our languages for an Accept-Language header: the ranges in order of their q value (the header's own order among equal ones), the first
    /// whose first part we have (de-AT is German); English when none is. q=0 means "not acceptable" and is skipped.
    /// </summary>
    public static string LanguageOf(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "en";
        var ranges = new List<(string Tag, double Q, int Order)>();
        var order = 0;
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            var q = 1.0;
            foreach (var parameter in pieces.Skip(1))
            {
                if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(parameter[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    q = parsed;
                }
            }

            ranges.Add((pieces[0], q, order++));
        }

        foreach (var (tag, q, _) in ranges.OrderByDescending(r => r.Q).ThenBy(r => r.Order))
        {
            if (q <= 0) continue;
            var primary = tag.Split('-')[0].ToLowerInvariant();
            if (Languages.Contains(primary)) return primary;
        }

        return "en";
    }

    public static MailMessageData Build(string language, string key, string to, params object[] args)
    {
        var table = Text.TryGetValue(language, out var t) ? t : Text["en"];
        var (subject, body) = table[key];
        return new MailMessageData(to, subject, string.Format(System.Globalization.CultureInfo.InvariantCulture, body, args));
    }
}
