using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Mail;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// What a real 500 (an exception the exception handler catches) answers: a problem body whose title is the framework's text, not
/// an API code, and which carries nothing of the exception. The web client keys its message on that (client.ts, toError).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed partial class ServerErrorBodyTests(ApiFactory api)
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ApiCode();

    [Fact]
    public async Task A_real_500_is_a_problem_body_whose_title_is_not_an_api_code_and_hides_the_exception()
    {
        var mailer = new FlakyMailer(api.Mail) { Down = true };
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        var client = JiraKit.ClientOf(api, host);

        var response = await client.PostAsync("/api/auth/register",
            new { email = Emails.New("server-error"), password = TestUser.Password, displayName = "Test Person" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("the mail server is down", text, StringComparison.Ordinal);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        var title = body.GetProperty("title").GetString();
        Assert.False(string.IsNullOrEmpty(title));
        Assert.DoesNotMatch(ApiCode(), title);
    }
}
