using System.Net;
using System.Security.Cryptography;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>Disconnect is the way out of a broken connection: it ends disconnected even when the stored tokens can no longer be opened.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationDisconnectTests(ApiFactory api)
{
    [Fact]
    public async Task Disconnect_with_registered_webhooks_and_a_token_that_cannot_be_opened_still_ends_disconnected()
    {
        var w = await JiraKit.ConnectedAsync(api);
        await using (var db = api.Db())
        {
            // As after the integration key was rotated: the sealed tokens no longer open.
            await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessToken, "AAAA").SetProperty(x => x.RefreshToken, "AAAA"));
            await db.IntegrationConnections.Where(c => c.Id == w.ConnectionId)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.WebhookIds, "{\"ids\":[11,12],\"spaces\":\"DCX\"}"));
        }

        var response = await w.Admin.SendAsync(HttpMethod.Delete, w.Base);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var connection = await JiraKit.ConnectionAsync(w);
        Assert.Equal((ConnectionStatuses.Disconnected, null), (connection.Status, connection.WebhookIds));
        await using var after = api.Db();
        Assert.Empty(await after.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId).ToListAsync());
    }

    [Fact]
    public async Task A_removal_with_a_token_that_cannot_be_opened_asks_for_a_new_sign_in_and_neither_reaches_the_caller_nor_logs_the_token()
    {
        var w = await JiraKit.ConnectedAsync(api);
        await using (var db = api.Db())
        {
            await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessToken, "AAAA").SetProperty(x => x.RefreshToken, "AAAA"));
            await db.IntegrationConnections.Where(c => c.Id == w.ConnectionId)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.WebhookIds, "{\"ids\":[21],\"spaces\":\"DCY\"}"));
        }

        await using var scope = api.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<PodiumDb>().IntegrationConnections.AsNoTracking().FirstAsync(c => c.Id == w.ConnectionId);
        var log = new ListLogger<WebhookManager>();
        var webhooks = ActivatorUtilities.CreateInstance<WebhookManager>(scope.ServiceProvider, log);

        await webhooks.TryRemoveAsync(connection, CancellationToken.None);

        // Since F-072 an unopenable token is a normal outcome (sign in again): the account is marked, the removal ends quietly, and nothing but a type name could be logged.
        await using var after = api.Db();
        Assert.All(await after.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId).ToListAsync(), a => Assert.Equal(AccountStatuses.Reauthorize, a.Status));
        Assert.All(log.Entries, line => Assert.DoesNotContain("AAAA", line.Text, StringComparison.Ordinal));
    }
}
