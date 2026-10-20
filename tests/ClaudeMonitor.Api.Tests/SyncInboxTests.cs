using ClaudeMonitor.Api.Integrations;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The in-memory list of "read this again" requests a webhook leaves for the worker (ADR-0015 §6).</summary>
public sealed class SyncInboxTests
{
    private static readonly Guid Connection = Guid.NewGuid();

    private static SyncRequest Issue(string key) => new(Connection, SyncTarget.Issue, key);

    private static async Task<SyncRequest> TakeAsync(SyncInbox inbox)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await inbox.TakeAsync(cts.Token);
    }

    [Fact]
    public async Task Requests_come_out_in_the_order_they_went_in()
    {
        var inbox = new SyncInbox();

        Assert.True(inbox.Enqueue(Issue("A-1")));
        Assert.True(inbox.Enqueue(new SyncRequest(Connection, SyncTarget.Sprint, "7")));
        Assert.True(inbox.Enqueue(Issue("A-2")));

        Assert.Equal("A-1", (await TakeAsync(inbox)).Ref);
        Assert.Equal(SyncTarget.Sprint, (await TakeAsync(inbox)).Target);
        Assert.Equal("A-2", (await TakeAsync(inbox)).Ref);
    }

    [Fact]
    public async Task The_same_request_waits_only_once_and_can_be_queued_again_once_taken()
    {
        var inbox = new SyncInbox();
        Assert.True(inbox.Enqueue(Issue("A-1")));

        Assert.False(inbox.Enqueue(Issue("A-1"))); // same connection, target and reference
        Assert.True(inbox.Enqueue(new SyncRequest(Guid.NewGuid(), SyncTarget.Issue, "A-1"))); // another connection
        Assert.True(inbox.Enqueue(new SyncRequest(Connection, SyncTarget.Sprint, "A-1"))); // another target
        await TakeAsync(inbox);

        Assert.True(inbox.Enqueue(Issue("A-1")));
    }

    [Fact]
    public async Task A_full_inbox_holds_its_capacity_in_order()
    {
        var inbox = new SyncInbox();
        for (var i = 0; i < SyncInbox.Capacity; i++) Assert.True(inbox.Enqueue(Issue("K-" + i)));

        Assert.Equal("K-0", (await TakeAsync(inbox)).Ref);
        Assert.True(inbox.Enqueue(Issue("K-0"))); // room again, and the taken request is no longer waiting
        for (var i = 1; i < SyncInbox.Capacity; i++) Assert.Equal("K-" + i, (await TakeAsync(inbox)).Ref);
        Assert.Equal("K-0", (await TakeAsync(inbox)).Ref);
    }

    [Fact]
    public async Task A_request_dropped_by_a_full_inbox_is_refused_and_can_be_queued_again_later()
    {
        var inbox = new SyncInbox();
        for (var i = 0; i < SyncInbox.Capacity; i++) Assert.True(inbox.Enqueue(Issue("K-" + i)));

        Assert.False(inbox.Enqueue(Issue("overflow")));
        await TakeAsync(inbox);
        Assert.True(inbox.Enqueue(Issue("overflow")));
    }

    [Fact]
    public async Task Taking_from_an_empty_inbox_waits_until_cancelled()
    {
        var inbox = new SyncInbox();
        using var cts = new CancellationTokenSource();
        var take = inbox.TakeAsync(cts.Token);
        Assert.False(take.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take);
    }

    [Fact]
    public void A_missing_request_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SyncInbox().Enqueue(null!));
    }
}
