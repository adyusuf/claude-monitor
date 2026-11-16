using System.Net;
using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Podium.Api.Tests;

/// <summary>S-4: inviting and creating a workspace have their own per-IP budget (AIPOD_INVITE_RATE_PER_MINUTE), apart from the sign-in budget.</summary>
[Collection(ApiGroup.Name)]
public sealed class InviteRateLimitTests(ApiFactory api)
{
    private const int AuthBudget = 3;
    private const int InviteBudget = 8;
    private static readonly string Invitations = "/api/workspaces/0190a1b2-0000-7000-8000-000000000001/invitations";

    private sealed class SeparateBudgetsFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AIPOD_AUTH_RATE_PER_MINUTE", AuthBudget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("AIPOD_INVITE_RATE_PER_MINUTE", InviteBudget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient http, string url) =>
        (await http.PostAsJsonAsync(url, new { })).StatusCode;

    [Fact]
    public async Task More_invitations_than_the_sign_in_budget_are_not_limited_and_the_two_budgets_are_apart()
    {
        Assert.NotNull(api); // the shared fixture has set the database and the other variables this API reads
        await using var host = new SeparateBudgetsFactory();
        var http = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var login = new List<HttpStatusCode>();
        for (var i = 0; i <= AuthBudget; i++) login.Add(await PostAsync(http, "/api/auth/login"));
        Assert.Equal(HttpStatusCode.TooManyRequests, login[^1]); // the sign-in budget is spent

        var invites = new List<HttpStatusCode>();
        for (var i = 0; i <= InviteBudget; i++) invites.Add(await PostAsync(http, Invitations));

        // With the sign-in budget spent, the invitations still answer (401: no session) up to their own budget, then 429.
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, invites.Take(InviteBudget));
        Assert.Equal(HttpStatusCode.TooManyRequests, invites[InviteBudget]);
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(http, "/api/auth/login")); // and spending theirs freed nothing of the sign-in's
    }

    [Fact]
    public async Task Creating_a_workspace_spends_the_invite_budget_not_the_sign_in_one()
    {
        Assert.NotNull(api);
        await using var host = new SeparateBudgetsFactory();
        var http = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < InviteBudget; i++) statuses.Add(await PostAsync(http, "/api/workspaces"));

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(http, "/api/auth/login"));
    }
}
