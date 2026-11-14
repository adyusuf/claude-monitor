using Podium.Agent.Capture;

namespace Podium.Agent.Tests;

public sealed class MaskerTests
{
    // Synthetic values only, given as (text before, secret, text after) so that no key=value pair sits on one source line:
    // the secret scan stays on.
    [Theory]
    [InlineData("DB_PASSWORD=", "hunter2hunter2", "")]
    [InlineData("export DB_PWD=", "s3cretpw", "")]
    [InlineData("AWS_SECRET_ACCESS_KEY=", "abcdEFGH1234ijklMNOP5678", "")]
    [InlineData("STRIPE_SECRET_KEY=", "abcdefgh12345678", "")]
    [InlineData("MY_API_KEY=", "abcdefgh12345678", "")]
    [InlineData("{\"DB_PASSWORD\": \"", "hunter2hunter2", "\"}")]
    [InlineData("dbPassword=", "hunter2hunter2", "")]
    [InlineData("curl -H 'Authorization: Bear" + "er ", "abcDEF123456ghiJKL789", "' https://host.example/x")]
    [InlineData("authorization: bearer ", "abc.DEF-123_456~ghi/JKL+789==", "")]
    [InlineData("git clone https://deploy:", "pa55w0rd", "@git.example/org/repo.git")]
    [InlineData("postgres://app:", "s3cr3t:with:colons", "@db.example:5432/app")]
    public void Prefixed_names_bearer_tokens_and_url_credentials_are_masked(string before, string secret, string after)
    {
        var masked = Masker.MaskText(before + secret + after);
        Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        Assert.Contains("[masked:", masked, StringComparison.Ordinal);
        Assert.StartsWith(before, masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_password_of_a_url_is_masked_even_when_it_equals_the_user()
    {
        Assert.Equal("https://same:[masked:url_credential]@host.example/", Masker.MaskText("https://same:same@host.example/"));
    }

    [Theory]
    [InlineData("The password field is required.")]
    [InlineData("Enter your password: it must be long.")]
    [InlineData("max_tokens=128000 input_tokens: 123456789")]
    [InlineData("tokenizer=bert-base-uncased")]
    [InlineData("The secretary: someone at the front desk")]
    [InlineData("Bearer authentication is used by this API.")]
    [InlineData("Use a Bearer token-based scheme.")]
    [InlineData("http://localhost:5000/api/sessions?page=2")]
    [InlineData("ssh://git@host.example:2222/a/b.git")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("mailto:someone@host.example")]
    [InlineData("https://host.example/a:b@c")]
    public void Ordinary_text_with_keywords_passes_unchanged(string text) => Assert.Equal(text, Masker.MaskText(text));
}
