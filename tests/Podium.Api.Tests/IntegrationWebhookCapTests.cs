using System.Net;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-075: a token without <c>exp</c> is still accepted (WebhookJwtTests pins that), so the receipts a connection may leave are capped per hour:
/// past the cap a new delivery is answered 429, leaves no receipt and queues nothing; a repeat is still 200, and older receipts do not count.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationWebhookCapTests(ApiFactory api)
{
    private async Task SeedAsync(JiraWorld w, int count, TimeSpan ago)
    {
        await using var db = api.Db();
        var at = api.Clock.GetUtcNow() - ago;
        db.IntegrationWebhookReceipts.AddRange(Enumerable.Range(0, count).Select(i => new IntegrationWebhookReceipt { ConnectionId = w.ConnectionId, DeliveryId = $"seed-{i}", ReceivedAt = at }));
        await db.SaveChangesAsync();
    }

    private async Task<int> ReceiptsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationWebhookReceipts.CountAsync(r => r.ConnectionId == w.ConnectionId);
    }

    [Fact]
    public async Task The_delivery_past_the_hourly_cap_is_answered_429_leaves_no_receipt_and_queues_nothing_and_a_repeat_is_still_200()
    {
        var w = await JiraKit.ConnectedAsync(api);
        await SeedAsync(w, IntegrationWebhookEndpoints.ReceiptsPerHour - 1, TimeSpan.FromMinutes(10));

        var last = await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("CAP-1"), "cap-last");
        var over = await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("CAP-2"), "cap-over");
        var repeat = await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("CAP-1"), "cap-last");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.OK), (last.StatusCode, over.StatusCode, repeat.StatusCode));
        Assert.Equal(IntegrationWebhookEndpoints.ReceiptsPerHour, await ReceiptsAsync(w));
        Assert.Equal("CAP-1", (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
        // CAP-2 was not queued: once the window has moved past the seeded receipts, the next delivery is the next request of the connection.
        api.Clock.Advance(IntegrationWebhookEndpoints.ReceiptWindow);
        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("CAP-3"), "cap-later")).StatusCode);
        Assert.Equal("CAP-3", (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
    }

    [Fact]
    public async Task Receipts_older_than_the_hour_and_another_connections_do_not_count()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var busy = await JiraKit.ConnectedAsync(api);
        await SeedAsync(w, IntegrationWebhookEndpoints.ReceiptsPerHour, IntegrationWebhookEndpoints.ReceiptWindow + TimeSpan.FromMinutes(1));
        await SeedAsync(busy, IntegrationWebhookEndpoints.ReceiptsPerHour, TimeSpan.FromMinutes(5));

        var answer = await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("OLD-1"), "old-ok");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("OLD-1", (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await WebhookKit.DeliverAsync(api, busy.ConnectionId, WebhookKit.IssueEvent("BSY-1"), "busy-over")).StatusCode);
    }
}
