using System.Net;
using System.Web;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class ExternalAuthTests(ApiFactory api)
{
    private static string Header(string subject, string email, bool verified, string name = "Provider Person") =>
        $"{subject}|{email}|{(verified ? "true" : "false")}|{name}";

    private static async Task<HttpResponseMessage> DoneAsync(TestUser client, string provider, string header, string mode = "signin")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/external/{provider}/done?mode={mode}");
        request.Headers.Add(TestExternal.Header, header);
        return await client.Http.SendAsync(request);
    }

    private static string Location(HttpResponseMessage r) => r.Headers.Location!.ToString();

    [Fact]
    public async Task Providers_are_listed_and_start_redirects_to_the_provider()
    {
        var client = api.NewClient();
        var available = await client.GetJsonAsync("/api/auth/providers");
        Assert.Equal(["github", "google"], available.GetProperty("available").EnumerateArray().Select(e => e.GetString()));
        var start = await client.Http.GetAsync("/api/auth/external/github");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize", Location(start), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.GetAsync("/api/auth/external/myspace")).StatusCode);
    }

    [Fact]
    public async Task A_new_verified_provider_account_becomes_a_user_with_a_workspace()
    {
        var client = api.NewClient();
        var email = Emails.New("gh-new");
        var done = await DoneAsync(client, "github", Header("gh-" + Guid.NewGuid(), email, true));
        Assert.Equal(ApiFactory.Origin + "/", Location(done));
        var me = await client.GetJsonAsync("/api/me");
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.False(me.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("github", me.GetProperty("providers")[0].GetString());
    }

    [Fact]
    public async Task An_existing_address_is_never_joined_silently()
    {
        var user = await api.NewClient().SignedUpAsync("gh-existing");
        var stranger = api.NewClient();
        var done = await DoneAsync(stranger, "google", Header("g-" + Guid.NewGuid(), user.Email, true));
        Assert.Contains("/login?error=link_required", Location(done), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task An_unverified_or_missing_address_is_refused()
    {
        var client = api.NewClient();
        Assert.Contains("provider_email_unverified", Location(await DoneAsync(client, "github", Header("s1", Emails.New("x"), false))),
            StringComparison.Ordinal);
        Assert.Contains("provider_email_unverified", Location(await DoneAsync(client, "github", Header("s2", "", true))),
            StringComparison.Ordinal);
        Assert.Contains("provider_failed", Location(await client.Http.GetAsync("/api/auth/external/github/done")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linking_then_signing_in_with_the_provider()
    {
        var user = await api.NewClient().SignedUpAsync("link");
        var subject = "gh-" + Guid.NewGuid();
        var linked = await DoneAsync(user, "github", Header(subject, "other@gmail.com", false), "link");
        Assert.EndsWith("/settings?linked=github", Location(linked), StringComparison.Ordinal);
        Assert.EndsWith("/settings?linked=github", Location(await DoneAsync(user, "github", Header(subject, "", false), "link")),
            StringComparison.Ordinal);

        var someoneElse = await api.NewClient().SignedUpAsync("link-other");
        Assert.Contains("provider_in_use", Location(await DoneAsync(someoneElse, "github", Header(subject, "", false), "link")),
            StringComparison.Ordinal);

        var fresh = api.NewClient();
        Assert.Contains("sign_in_first", Location(await DoneAsync(fresh, "github", Header(subject, "", false), "link")),
            StringComparison.Ordinal);
        Assert.Equal(ApiFactory.Origin + "/", Location(await DoneAsync(fresh, "github", Header(subject, "", false))));
        Assert.Equal(user.Id, (await fresh.GetJsonAsync("/api/me")).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("github")]
    [InlineData("google")]
    public async Task The_whole_round_trip_through_the_real_oauth_handler(string provider)
    {
        var email = Emails.New("rt-" + provider);
        ProviderStub.GitHubEmail = email;
        ProviderStub.GoogleEmail = email;
        var client = api.NewClient();
        var start = await client.Http.GetAsync($"/api/auth/external/{provider}");
        var state = HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"];
        var callback = await client.Http.GetAsync($"/api/auth/callback/{provider}?code=abc&state={Uri.EscapeDataString(state!)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var done = await client.Http.GetAsync(callback.Headers.Location!.ToString());
        Assert.Equal(ApiFactory.Origin + "/", Location(done));
        Assert.Equal(email, (await client.GetJsonAsync("/api/me")).GetProperty("email").GetString());
    }
}
