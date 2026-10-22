using Podium.Api.Mail;

namespace Podium.Api.Tests;

/// <summary>The mails exist in English, Turkish and German; the site is called Podium; the language comes from Accept-Language.</summary>
public sealed class MailTemplatesTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("tr-TR,tr;q=0.9,en;q=0.8", "tr")]
    [InlineData("de-AT,de;q=0.9,en;q=0.5", "de")]
    [InlineData("DE", "de")]
    [InlineData("fr-FR,fr;q=0.9,de;q=0.8", "de")] // the first language of ours, by preference
    [InlineData("fr-FR,fr;q=0.9", "en")]
    [InlineData("en;q=0.5,de;q=0.9", "de")] // q decides, not the order
    [InlineData("de;q=0,tr;q=0.1", "tr")] // q=0 is refused
    [InlineData("de;q=abc", "de")] // an unreadable q counts as 1
    [InlineData("*", "en")]
    public void The_language_is_the_best_of_ours_by_preference_and_English_otherwise(string? header, string language) =>
        Assert.Equal(language, MailTemplates.LanguageOf(header));

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    [InlineData("de")]
    public void Every_mail_exists_in_every_language_with_its_placeholders_filled_and_the_site_named_Podium(string language)
    {
        foreach (var key in new[] { "verify", "reset", "invite" })
        {
            var mail = MailTemplates.Build(language, key, "a@b.co", "https://link.example/x", "Ada", "Team");
            Assert.Equal("a@b.co", mail.To);
            Assert.False(string.IsNullOrWhiteSpace(mail.Subject));
            Assert.Contains("https://link.example/x", mail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", mail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Monitor", mail.Body, StringComparison.Ordinal);
        }

        Assert.Contains("Podium", MailTemplates.Build(language, "verify", "a@b.co", "https://x").Body, StringComparison.Ordinal);
        var invite = MailTemplates.Build(language, "invite", "a@b.co", "https://x", "Ada", "Team").Body;
        Assert.Contains("Ada", invite, StringComparison.Ordinal);
        Assert.Contains("\"Team\"", invite, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_language_falls_back_to_English_and_the_list_of_languages_is_what_has_text()
    {
        Assert.Equal(MailTemplates.Build("en", "reset", "a@b.co", "https://x"), MailTemplates.Build("fr", "reset", "a@b.co", "https://x"));
        Assert.Equal(["en", "tr", "de"], MailTemplates.Languages);
        Assert.Equal("Podium", Podium.Api.Config.ApiConfig.MfaIssuer);
    }
}
