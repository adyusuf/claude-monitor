using System.Net;
using System.Web;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A provider sign-in returns to the same-site page it started for (an invitation link), never off the site (ExternalAuthEndpoints).</summary>
[Collection(ApiGroup.Name)]
public sealed class ExternalAuthNextTests(ApiFactory api)
{
    private const string Next = "/invitations/accept?token=abc";

    private static string Header(string subject, string email) => $"{subject}|{email}|true|Provider Person";

    private static async Task<HttpResponseMessage> DoneAsync(TestUser client, string header, string? next, string mode = "signin")
    {
        var query = next is null ? "" : "&next=" + Uri.EscapeDataString(next);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/external/github/done?mode={mode}{query}");
        request.Headers.Add(TestExternal.Header, header);
        return await client.Http.SendAsync(request);
    }

    private static string Location(HttpResponseMessage r) => r.Headers.Location!.ToString();

    [Fact]
    public async Task A_new_and_a_returning_provider_user_land_on_the_page_they_started_for()
    {
        var subject = "gh-" + Guid.NewGuid();
        var email = Emails.New("gh-next");

        Assert.Equal(ApiFactory.Origin + Next, Location(await DoneAsync(api.NewClient(), Header(subject, email), Next)));
        Assert.Equal(ApiFactory.Origin + Next, Location(await DoneAsync(api.NewClient(), Header(subject, email), Next)));
    }

    [Theory]
    [InlineData("//evil.example.net")]
    [InlineData("/\\evil.example.net")]
    [InlineData("/\t/evil.example.net")]
    [InlineData("@evil.example.net")]
    [InlineData("https://evil.example.net")]
    [InlineData("")]
    public async Task An_unsafe_next_falls_back_to_the_start_page(string next)
    {
        var done = await DoneAsync(api.NewClient(), Header("gh-" + Guid.NewGuid(), Emails.New("gh-unsafe")), next);

        Assert.Equal(ApiFactory.Origin + "/", Location(done));
    }

    [Fact]
    public async Task With_two_step_sign_in_the_next_page_rides_along_to_the_second_step()
    {
        var subject = "gh-" + Guid.NewGuid();
        var user = await api.NewClient().SignedUpAsync("gh-next-mfa");
        await DoneAsync(user, Header(subject, ""), null, "link");
        await RemoteKit.EnableTotpAsync(api, user);

        var done = await DoneAsync(api.NewClient(), Header(subject, ""), Next);

        Assert.Equal(ApiFactory.Origin + "/mfa?next=" + Uri.EscapeDataString(Next), Location(done));
    }

    [Fact]
    public async Task The_next_page_survives_the_real_oauth_round_trip()
    {
        ProviderStub.GitHubEmail = Emails.New("rt-next");
        var client = api.NewClient();
        var start = await client.Http.GetAsync("/api/auth/external/github?mode=signin&next=" + Uri.EscapeDataString(Next));
        var state = HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"];
        var callback = await client.Http.GetAsync($"/api/auth/callback/github?code=abc&state={Uri.EscapeDataString(state!)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        var done = await client.Http.GetAsync(callback.Headers.Location!.ToString());

        Assert.Equal(ApiFactory.Origin + Next, Location(done));
    }
}
