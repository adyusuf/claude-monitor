using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>
/// F-234: two API instances share no in-process lock, so a refresh is also serialized on the account row in the database. While another
/// instance holds the row (mid-refresh), this one waits, then hands out the token that instance saved instead of refreshing again with the
/// refresh token that rotation has spent.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationTokenInstanceTests(ApiFactory api)
{
    private ApiConfig Config => api.Services.GetRequiredService<ApiConfig>();

    private static int RefreshesWith(FakeJira jira, string refreshToken) =>
        jira.Requests.Count(r => r.Path == "/oauth/token" && r.Body.Contains($"\"refresh_token\":\"{refreshToken}\"", StringComparison.Ordinal));

    [Fact]
    public async Task A_refresh_waits_for_another_instance_holding_the_account_and_hands_out_the_token_that_instance_saved()
    {
        var w = await JiraKit.ConnectedAsync(api);
        IntegrationAccount account;
        await using (var read = api.Db())
        {
            account = await read.IntegrationAccounts.AsNoTracking().SingleAsync(a => a.ConnectionId == w.ConnectionId && a.UserId == w.Admin.Id);
        }

        var oldRefresh = IntegrationTokens.Open(Config, account.RefreshToken);
        var expired = api.Clock.GetUtcNow().AddMinutes(-1);
        var valid = api.Clock.GetUtcNow().AddHours(1);
        var theirs = IntegrationTokens.Seal(Config, "at-other-instance");
        await using var other = api.Db(); // the other instance: it holds the account row while it refreshes
        await other.IntegrationAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessExpiresAt, expired));
        await using var held = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM integration_accounts WHERE id = {account.Id} FOR UPDATE");

        await using var scope = api.Services.CreateAsyncScope();
        var mine = scope.ServiceProvider.GetRequiredService<IntegrationTokens>().AccessAsync(account.Id, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(mine.IsCompleted, "the token was handed out while another instance held the account");
        await other.IntegrationAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(a => a
            .SetProperty(x => x.AccessToken, theirs).SetProperty(x => x.AccessExpiresAt, valid));
        await held.CommitAsync();

        Assert.Equal("at-other-instance", await mine.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, RefreshesWith(api.Jira, oldRefresh)); // no second refresh with the token the other instance used
    }
}
