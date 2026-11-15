using System.Net;
using Podium.Api.Config;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>The callback ends in a redirect to the web page even when the failure is not the tracker's (F-072): never a 500 after the one-use state is spent.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationCallbackFailureTests(ApiFactory api)
{
    private ApiConfig Config => api.Services.GetRequiredService<ApiConfig>();

    private async Task<int> CountConnectionsAsync(Guid workspace)
    {
        await using var db = api.Db();
        return await db.IntegrationConnections.CountAsync(c => c.WorkspaceId == workspace);
    }

    [Fact]
    public async Task A_state_sealed_under_an_integration_key_that_was_rotated_since_redirects_as_unavailable()
    {
        var admin = await api.NewClient().SignedUpAsync("icf-rotated");
        var site = api.Jira.Site();
        var account = api.Jira.Account("Rotated", admin.Email, site);
        var url = await JiraKit.StartAsync(admin, site.Subdomain);
        var otherKey = Config with { IntegrationKey = new byte[32] };
        await using (var db = api.Db())
        {
            var resealed = IntegrationTokens.Seal(otherKey, "verifier-under-the-old-key");
            await db.IntegrationOAuthStates.Where(s => s.UserId == admin.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Verifier, resealed));
        }

        var response = await JiraKit.ApproveAsync(admin, api.Jira, account, url);

        Assert.Equal(TrackerException.Codes.Unavailable, JiraKit.ResultOf(response));
        Assert.StartsWith($"{ApiFactory.Origin}/w/{admin.WorkspaceId}/integrations?result=", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await CountConnectionsAsync(admin.WorkspaceId));
        await using var check = api.Db();
        Assert.Empty(await check.IntegrationOAuthStates.Where(s => s.UserId == admin.Id).ToListAsync()); // the state stays spent
    }

    [Fact]
    public async Task Two_admins_connecting_the_same_new_site_at_once_both_come_back_with_a_redirect_and_one_connection_is_made()
    {
        var owner = await api.NewClient().SignedUpAsync("icf-race");
        var admin = await RemoteKit.MemberAsync(api, owner, "icf-race-admin", "admin");
        var site = api.Jira.Site();
        var first = api.Jira.Account("First", owner.Email, site);
        var second = api.Jira.Account("Second", admin.Email, site);
        var urls = (await JiraKit.StartAsync(owner, site.Subdomain), await JiraKit.StartAsync(admin, site.Subdomain, owner.WorkspaceId));
        api.Jira.TokenDelay = TimeSpan.FromMilliseconds(300); // both exchanges are in flight together, so both look for the connection at once
        try
        {
            var answers = await Task.WhenAll(JiraKit.ApproveAsync(owner, api.Jira, first, urls.Item1), JiraKit.ApproveAsync(admin, api.Jira, second, urls.Item2));

            Assert.All(answers, a => Assert.Equal(HttpStatusCode.Redirect, a.StatusCode));
            Assert.Contains("ok", answers.Select(JiraKit.ResultOf));
            Assert.All(answers.Select(JiraKit.ResultOf), r => Assert.Contains(r, new[] { "ok", TrackerException.Codes.Unavailable }));
            Assert.Equal(1, await CountConnectionsAsync(owner.WorkspaceId));
        }
        finally
        {
            api.Jira.TokenDelay = TimeSpan.Zero;
        }
    }
}
