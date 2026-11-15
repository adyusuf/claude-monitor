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
    public async Task A_removal_that_fails_for_a_reason_that_is_not_the_tracker_s_is_logged_by_its_type_only_and_does_not_reach_the_caller()
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

        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains(nameof(CryptographicException), line.Text, StringComparison.Ordinal);
        Assert.Contains(w.ConnectionId.ToString(), line.Text, StringComparison.Ordinal);
    }
}
