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

/// <summary>A sign-in callback that fails (ADR-0017): a provider that is not set answers 404, one that is set sends the person back to the sign-in page. Never a 500.</summary>
[Collection(ApiGroup.Name)]
public sealed class ExternalProviderFailureTests(ApiFactory api) : IAsyncLifetime
{
    private const string FailedLogin = "/login?error=provider_failed";

    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    /// <summary>The handler the API registered for the scheme, run over a context whose services hold these settings.</summary>
    private async Task<(HttpResponse Response, RemoteFailureContext Context)> FailAsync(ApiConfig config, string scheme)
    {
        var services = new ServiceCollection().AddSingleton(config).AddSingleton(ConfigKit.Settings(config)).AddSingleton<ILoggerFactory>(new TracingLog()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var context = new RemoteFailureContext(http, new AuthenticationScheme(scheme, scheme, typeof(OAuthHandler<OAuthOptions>)), new OAuthOptions(),
            new InvalidOperationException("The oauth state was missing or invalid."));

        await api.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(scheme).Events.OnRemoteFailure(context);

        return (http.Response, context);
    }

    private static (string Key, string? Value)[] App(string prefix) => [($"AIPOD_{prefix}_CLIENT_ID", "app-id"), ($"AIPOD_{prefix}_CLIENT_SECRET", "app-secret")];

    private static async Task<string[]> OfferedAsync(TestUser client) =>
        [.. (await client.GetJsonAsync("/api/auth/providers")).GetProperty("available").EnumerateArray().Select(e => e.GetString()!)];

    // ---- the handler itself ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("github")]
    [InlineData("google")]
    public async Task A_failed_callback_of_a_provider_that_is_not_set_is_a_404_with_no_redirect_and_the_response_is_handled(string scheme)
    {
        var (response, context) = await FailAsync(ConfigKit.Prod(), scheme);

        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.False(response.Headers.ContainsKey("Location"));
        Assert.True(context.Result is { Handled: true });
    }

    [Theory]
    [InlineData("github", "GITHUB")]
    [InlineData("google", "GOOGLE")]
    public async Task A_failed_callback_of_a_provider_that_is_set_goes_back_to_the_sign_in_page_and_the_response_is_handled(string scheme, string prefix)
    {
        var (response, context) = await FailAsync(ConfigKit.Prod(App(prefix)), scheme);

        Assert.Equal(StatusCodes.Status302Found, response.StatusCode);
        Assert.Equal("https://podium.test.invalid" + FailedLogin, response.Headers.Location.ToString());
        Assert.True(context.Result is { Handled: true });
    }

    [Fact]
    public async Task Only_the_failing_providers_own_app_decides_between_404_and_the_redirect()
    {
        var githubOnly = ConfigKit.Prod(App("GITHUB"));

        Assert.Equal(StatusCodes.Status302Found, (await FailAsync(githubOnly, "github")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await FailAsync(githubOnly, "google")).Response.StatusCode);
        var half = ConfigKit.Prod(("AIPOD_GITHUB_CLIENT_ID", "only-an-id")); // an id alone is no app
        Assert.Equal(StatusCodes.Status404NotFound, (await FailAsync(half, "github")).Response.StatusCode);
    }

    // ---- through the running host ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("github", "")]
    [InlineData("github", "?error=access_denied")]
    [InlineData("github", "?code=abc&state=not-a-state")]
    [InlineData("google", "")]
    [InlineData("google", "?error=access_denied&state=x")]
    public async Task A_callback_without_a_valid_state_on_a_provider_that_is_set_redirects_to_the_sign_in_page(string provider, string query)
    {
        var client = api.NewClient();

        var response = await client.Http.GetAsync("/api/auth/callback/" + provider + query);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ApiFactory.Origin + FailedLogin, response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_callback_on_a_provider_whose_app_is_not_set_is_404_never_500_and_the_other_provider_still_redirects()
    {
        await using var host = HostKit.WithConfig(api, c => HostKit.WithoutEnvOf(c, "github"));
        var client = JiraKit.ClientOf(api, host);

        Assert.Equal(["google"], await OfferedAsync(client)); // the override reached the settings this host runs on
        var response = await client.Http.GetAsync("/api/auth/callback/github");
        var withState = await client.Http.GetAsync("/api/auth/callback/github?code=abc&state=not-a-state");
        var other = await client.Http.GetAsync("/api/auth/callback/google");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withState.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        Assert.Equal(ApiFactory.Origin + FailedLogin, other.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync("/api/auth/external/github")).StatusCode);
    }

    // ---- an app saved on the page reaches the real handler --------------------------------------------------------

    [Fact]
    public async Task A_github_app_saved_on_the_page_is_offered_and_used_at_once_a_changed_one_replaces_it_and_removing_it_switches_the_provider_off()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ep-page-app");
        await using var host = HostKit.WithConfig(api, c => HostKit.WithoutEnvOf(c, "github"));
        var client = await HostKit.SignInAsync(api, host, admin);
        Assert.Equal(["google"], await OfferedAsync(client));
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync("/api/auth/external/github")).StatusCode);

        var saved = await ServerAdminKit.PutAsync(client, "github", new { client_id = "saved-gh-id", client_secret = "saved-gh-secret" });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["github", "google"], await OfferedAsync(client));
        var start = await client.Http.GetAsync("/api/auth/external/github");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.StartsWith(ProviderEndpoints.GitHubAuthorize, start.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("saved-gh-id", JiraKit.Param(start.Headers.Location.ToString(), "client_id"));
        Assert.EndsWith("/api/auth/callback/github", JiraKit.Param(start.Headers.Location.ToString(), "redirect_uri"), StringComparison.Ordinal);
        // A callback that fails now goes back to the sign-in page: the app is in force.
        var callback = await client.Http.GetAsync("/api/auth/callback/github");
        Assert.Equal(ApiFactory.Origin + FailedLogin, callback.Headers.Location!.ToString());

        (await ServerAdminKit.PutAsync(client, "github", new { client_id = "second-gh-id" })).EnsureSuccessStatusCode();
        var again = await client.Http.GetAsync("/api/auth/external/github");
        Assert.Equal("second-gh-id", JiraKit.Param(again.Headers.Location!.ToString(), "client_id"));

        var removed = await ServerAdminKit.PutAsync(client, "github", new { client_id = "", client_secret = "" });

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(["google"], await OfferedAsync(client));
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync("/api/auth/external/github")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync("/api/auth/callback/github")).StatusCode);
    }

    [Fact]
    public async Task A_google_app_saved_on_the_page_is_offered_next_to_the_apps_of_the_environment_without_touching_them()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "ep-page-google");
        await using var host = HostKit.WithConfig(api, c => HostKit.WithoutEnvOf(c, "google"));
        var client = await HostKit.SignInAsync(api, host, admin);
        Assert.Equal(["github"], await OfferedAsync(client));

        (await ServerAdminKit.PutAsync(client, "google", new { client_id = "saved-g-id", client_secret = "saved-g-secret" })).EnsureSuccessStatusCode();

        Assert.Equal(["github", "google"], await OfferedAsync(client));
        var start = await client.Http.GetAsync("/api/auth/external/google");
        Assert.StartsWith(ProviderEndpoints.GoogleAuthorize, start.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("saved-g-id", JiraKit.Param(start.Headers.Location.ToString(), "client_id"));
        var github = await client.Http.GetAsync("/api/auth/external/github");
        Assert.Equal("gh-id", JiraKit.Param(github.Headers.Location!.ToString(), "client_id")); // the environment's app is as it was
    }
}
