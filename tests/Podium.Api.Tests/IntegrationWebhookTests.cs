using System.Net;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The address Jira calls (ADR-0015 §6): the signed token is the only credential, a delivery counts once, and the body only names what to read again.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationWebhookTests(ApiFactory api)
{
    private static string Bearer(string payload, string secret) => "Bearer " + WebhookJwt.Sign(payload, secret);

    /// <summary>Proves that nothing was queued before: the next request of the connection is the one sent last.</summary>
    private async Task AssertNothingQueuedBeforeAsync(JiraWorld w, string key)
    {
        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent(key))).StatusCode);
        Assert.Equal(key, (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
    }

    private async Task<int> ReceiptsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationWebhookReceipts.CountAsync(r => r.ConnectionId == w.ConnectionId);
    }

    [Fact]
    public async Task A_call_without_a_good_token_is_refused_and_leaves_no_trace()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var future = $"{{\"exp\":{api.Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds()}}}";
        var expired = $"{{\"exp\":{api.Clock.GetUtcNow().AddHours(-1).ToUnixTimeSeconds()}}}";
        var body = WebhookKit.IssueEvent("ABC-1");

        foreach (var header in new[] { "", "Bearer garbage", "Bearer a.b.c", Bearer(future, "another-secret"), Bearer(expired, FakeJira.ClientSecret), "Basic " + WebhookKit.Token(api)[7..] })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookKit.DeliverAsync(api, w.ConnectionId, body, "d-1", header)).StatusCode);
        }

        Assert.Equal(0, await ReceiptsAsync(w));
        // The token is judged before the connection is looked at: a wrong token cannot tell a real connection from a made-up one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookKit.DeliverAsync(api, Guid.NewGuid(), body, null, "Bearer garbage")).StatusCode);
        await AssertNothingQueuedBeforeAsync(w, "ABC-9");
    }

    [Fact]
    public async Task A_good_token_for_a_connection_that_is_unknown_disconnected_or_must_sign_in_again_is_a_404()
    {
        var gone = await JiraKit.ConnectedAsync(api);
        var again = await JiraKit.ConnectedAsync(api);
        (await gone.Admin.SendAsync(HttpMethod.Delete, gone.Base)).EnsureSuccessStatusCode();
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == again.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Reauthorize));
        }

        foreach (var id in new[] { Guid.NewGuid(), gone.ConnectionId, again.ConnectionId })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await WebhookKit.DeliverAsync(api, id, WebhookKit.IssueEvent("ABC-1"), "d-x")).StatusCode);
        }

        Assert.Equal((0, 0), (await ReceiptsAsync(gone), await ReceiptsAsync(again)));
    }

    [Fact]
    public async Task A_delivery_queues_one_read_of_the_issue_and_the_same_delivery_again_is_not_processed_twice()
    {
        var w = await JiraKit.ConnectedAsync(api);

        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-1"), "delivery-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-2"), "delivery-1")).StatusCode); // a repeat of the id
        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-3", "jira:issue_created"), "delivery-2")).StatusCode);

        var first = await WebhookKit.NextAsync(api, w.ConnectionId);
        var second = await WebhookKit.NextAsync(api, w.ConnectionId);
        Assert.Equal((SyncTarget.Issue, "ABC-1"), (first.Target, first.Ref));
        Assert.Equal("ABC-3", second.Ref); // ABC-2 was never queued
        await using var db = api.Db();
        Assert.Equal(["delivery-1", "delivery-2"], (await db.IntegrationWebhookReceipts.Where(r => r.ConnectionId == w.ConnectionId).Select(r => r.DeliveryId).ToListAsync()).Order());
    }

    [Fact]
    public async Task A_delivery_without_an_identifier_is_known_by_its_body()
    {
        var w = await JiraKit.ConnectedAsync(api);

        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-1"));
        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-1")); // the same bytes: the same delivery
        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-2"));

        Assert.Equal("ABC-1", (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
        Assert.Equal("ABC-2", (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
        Assert.Equal(2, await ReceiptsAsync(w));
    }

    [Fact]
    public async Task A_sprint_event_queues_the_sprint_by_its_number()
    {
        var w = await JiraKit.ConnectedAsync(api);

        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.SprintEvent("12", "sprint_started"), "s-1");
        await WebhookKit.DeliverAsync(api, w.ConnectionId, "{\"webhookEvent\":\"sprint_closed\",\"sprint\":{\"id\":\"34\"}}", "s-2");

        var first = await WebhookKit.NextAsync(api, w.ConnectionId);
        var second = await WebhookKit.NextAsync(api, w.ConnectionId);
        Assert.Equal((SyncTarget.Sprint, "12"), (first.Target, first.Ref));
        Assert.Equal((SyncTarget.Sprint, "34"), (second.Target, second.Ref));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"webhookEvent\":\"comment_created\",\"issue\":{\"key\":\"ABC-1\"}}")]
    [InlineData("{\"webhookEvent\":\"jira:issue_updated\"}")]
    [InlineData("{\"webhookEvent\":\"jira:issue_updated\",\"issue\":{}}")]
    [InlineData("{\"webhookEvent\":\"jira:issue_updated\",\"issue\":{\"key\":\"\"}}")]
    [InlineData("{\"webhookEvent\":\"sprint_updated\"}")]
    [InlineData("{\"webhookEvent\":\"sprint_updated\",\"sprint\":{\"id\":\"1x\"}}")]
    [InlineData("{\"webhookEvent\":\"sprint_updated\",\"sprint\":{\"id\":\"\"}}")]
    [InlineData("{\"issue\":{\"key\":\"ABC-1\"},\"sprint\":{\"id\":1}}")]
    public async Task A_body_that_names_nothing_to_read_is_accepted_and_changes_nothing(string body)
    {
        var w = await JiraKit.ConnectedAsync(api);

        Assert.Equal(HttpStatusCode.OK, (await WebhookKit.DeliverAsync(api, w.ConnectionId, body, "junk")).StatusCode);

        await AssertNothingQueuedBeforeAsync(w, "ABC-7");
    }

    [Fact]
    public async Task A_key_or_a_sprint_number_that_is_too_long_is_not_queued()
    {
        var w = await JiraKit.ConnectedAsync(api);

        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("A-" + new string('1', 63)), "long-key"); // 65 characters
        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.SprintEvent(new string('1', 21)), "long-id");
        await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("A-" + new string('1', 62)), "edge-key"); // 64: the longest accepted

        Assert.Equal("A-" + new string('1', 62), (await WebhookKit.NextAsync(api, w.ConnectionId)).Ref);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_with_413_and_not_remembered()
    {
        var w = await JiraKit.ConnectedAsync(api);

        var atLimit = await WebhookKit.DeliverAsync(api, w.ConnectionId, new string(' ', IntegrationWebhookEndpoints.BodyMax), "at-limit");
        var over = await WebhookKit.DeliverAsync(api, w.ConnectionId, new string(' ', IntegrationWebhookEndpoints.BodyMax + 1), "over-limit");
        var hugeWithKey = await WebhookKit.DeliverAsync(api, w.ConnectionId, WebhookKit.IssueEvent("ABC-1").TrimEnd('}') + new string(' ', IntegrationWebhookEndpoints.BodyMax) + "}", "huge");

        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hugeWithKey.StatusCode);
        await using var db = api.Db();
        Assert.Equal(["at-limit"], await db.IntegrationWebhookReceipts.Where(r => r.ConnectionId == w.ConnectionId).Select(r => r.DeliveryId).ToListAsync());
        await AssertNothingQueuedBeforeAsync(w, "ABC-8");
    }
}
