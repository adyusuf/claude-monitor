using System.Net;
using Podium.Api.Config;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Podium.Api.Tests;

/// <summary>What a failed sign-in callback leaves in the log and where it sends the person (ADR-0017): the type of the failure, never its message; Settings for a link, the sign-in page otherwise.</summary>
[Collection(ApiGroup.Name)]
public sealed class ExternalProviderLogTests(ApiFactory api) : IAsyncLifetime
{
    private const string Secret = "s3cr3t-code-0f9a7c";
    private static readonly string Category = typeof(ExternalProviders).FullName!;

    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private static (string Key, string? Value)[] GitHubApp() => [("AIPOD_GITHUB_CLIENT_ID", "app-id"), ("AIPOD_GITHUB_CLIENT_SECRET", "app-secret")];

    /// <summary>The handler the API registered for GitHub, run over a context whose services hold these settings and this log.</summary>
    private async Task<(HttpResponse Response, TracingLog Log)> FailAsync(ApiConfig config, Exception? failure = null, string query = "", string? redirectUri = null)
    {
        var log = new TracingLog();
        var services = new ServiceCollection().AddSingleton(config).AddSingleton(ConfigKit.Settings(config)).AddSingleton<ILoggerFactory>(log).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.QueryString = new QueryString(query);
        var context = new RemoteFailureContext(http, new AuthenticationScheme("github", "github", typeof(OAuthHandler<OAuthOptions>)), new OAuthOptions(),
            failure ?? new InvalidOperationException("boom"))
        {
            Properties = redirectUri is null ? null : new AuthenticationProperties { RedirectUri = redirectUri },
        };

        await api.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("github").Events.OnRemoteFailure(context);

        return (http.Response, log);
    }

    [Fact]
    public async Task A_failure_is_logged_once_as_a_warning_that_names_the_type_and_the_message_with_a_secret_in_it_is_nowhere()
    {
        var (response, log) = await FailAsync(ConfigKit.Prod(GitHubApp()), new InvalidOperationException("the provider answered code=" + Secret));

        var entry = Assert.Single(log.Entries);
        Assert.Equal((LogLevel.Warning, Category), (entry.Level, entry.Category));
        Assert.Contains("github", entry.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), entry.Text, StringComparison.Ordinal);
        Assert.All(log.Entries, e =>
        {
            Assert.DoesNotContain(Secret, e.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, e.Everything, StringComparison.Ordinal); // not in the state, not in an exception attached to it
        });
        Assert.Equal(StatusCodes.Status302Found, response.StatusCode);
    }

    [Fact]
    public async Task A_person_who_said_no_at_the_provider_is_logged_as_information_not_a_warning()
    {
        var (response, log) = await FailAsync(ConfigKit.Prod(GitHubApp()), query: "?error=access_denied&error_description=" + Secret);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("cancelled by the person", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.DoesNotContain(Secret, entry.Everything, StringComparison.Ordinal);
        Assert.Equal("https://podium.test.invalid/login?error=provider_failed", response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/api/auth/external/github/done?mode=link", "/settings?error=provider_failed")]
    [InlineData("/api/auth/external/github/done?mode=signin", "/login?error=provider_failed")]
    [InlineData("/", "/login?error=provider_failed")]
    [InlineData(null, "/login?error=provider_failed")]
    public async Task A_failed_link_goes_back_to_settings_and_any_other_failure_to_the_sign_in_page(string? redirectUri, string expected)
    {
        var (response, _) = await FailAsync(ConfigKit.Prod(GitHubApp()), redirectUri: redirectUri);

        Assert.Equal(StatusCodes.Status302Found, response.StatusCode);
        Assert.Equal("https://podium.test.invalid" + expected, response.Headers.Location.ToString());
    }

    [Fact]
    public async Task A_provider_that_is_not_set_is_404_and_logs_nothing_at_all()
    {
        var (response, log) = await FailAsync(ConfigKit.Prod(), new InvalidOperationException(Secret), "?error=access_denied");

        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Through_the_host_a_bad_callback_is_logged_as_a_warning_and_redirects_and_a_refusal_is_information()
    {
        var log = new TracingLog();
        await using var host = HostKit.WithInterceptor(api, new CommandFault("never-matches"), log);
        var client = JiraKit.ClientOf(api, host);

        var bad = await client.Http.GetAsync("/api/auth/callback/github?code=" + Secret + "&state=not-a-state");
        var refused = await client.Http.GetAsync("/api/auth/callback/google?error=access_denied");

        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Equal(ApiFactory.Origin + "/login?error=provider_failed", bad.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        var ours = log.Entries.Where(e => e.Category == Category).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Information], ours.Select(e => e.Level));
        Assert.Contains("github", ours[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, e => e.Everything.Contains(Secret, StringComparison.Ordinal) && e.Category == Category);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }
}
