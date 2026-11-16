using System.Net;
using System.Text.Json;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Push;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-239: an event the API refuses for good is moved aside and counted instead of blocking the upload for ever, and the agent honours the batch size the API tells it.</summary>
public sealed partial class RelayTests
{
    private List<EventBatch> BatchesSent() =>
        [.. fake.Seen.Where(s => s.Path == "/api/agent/batches").Select(s => JsonSerializer.Deserialize<EventBatch>(s.Body, ApiClient.Json)!)];

    private readonly List<int> accepted = [];

    /// <summary>The API refuses (with <paramref name="refusal"/>) any batch that holds the event numbered 0 and takes every other (their numbers are kept in <see cref="accepted"/>).</summary>
    private void RefuseEventZero(HttpStatusCode refusal) => fake.On("POST /api/agent/batches", body =>
    {
        var batch = JsonSerializer.Deserialize<EventBatch>(body, ApiClient.Json)!;
        var numbers = batch.Events.Select(e => e.Payload.GetProperty("i").GetInt32()).ToList();
        if (numbers.Contains(0)) return (refusal, """{"title":"invalid_event"}""");
        accepted.AddRange(numbers);
        return (HttpStatusCode.OK, JsonSerializer.Serialize(new BatchAck(batch.BatchSeq, false, batch.Events.Count), ApiClient.Json));
    });

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task One_event_refused_for_good_is_moved_aside_after_three_tries_and_the_events_behind_it_go_up(HttpStatusCode refusal)
    {
        Queue("s", 3);
        RefuseEventZero(refusal);

        // The batch of two is halved down to the event alone, which fails twice (the loop's backoff lies between the tries) ...
        await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        Assert.Equal(3, store.OutboxCount());
        Assert.Equal(0, store.QuarantineCount());

        // ... and the third time it is moved aside; the two behind it are sent in the same pass.
        Assert.Equal(2, await relay.UploadAsync(CancellationToken.None));

        Assert.Equal(0, store.OutboxCount());
        Assert.Equal(1, store.QuarantineCount());
        Assert.Equal(["hook:Stop"], store.QuarantinedKinds());
        Assert.Equal(1, store.Count(LocalStore.QuarantinedKey));
        Assert.Equal([1, 2], accepted); // each good event went up once, in order
    }

    [Fact]
    public async Task The_log_names_the_moved_event_by_its_kind_and_never_by_its_content()
    {
        store.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, "s", "hook:Stop", clock.GetUtcNow(), JsonSerializer.SerializeToElement(new { i = 0, secret = "do-not-log-this-text" })),
            """{"i":0,"secret":"do-not-log-this-text"}""");
        RefuseEventZero(HttpStatusCode.BadRequest);
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        await relay.UploadAsync(CancellationToken.None);

        var log = File.ReadAllText(home.Config.LogPath);
        Assert.Contains("moved aside", log, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", log, StringComparison.Ordinal);
        Assert.Contains("hook:Stop", log, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-text", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_event_is_never_moved_aside_for_an_answer_that_may_pass(HttpStatusCode transient)
    {
        Queue("s", 1);
        fake.On("POST /api/agent/batches", transient, "{}");
        for (var i = 0; i < 6; i++) await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));

        Assert.Equal(1, store.OutboxCount());
        Assert.Equal(0, store.QuarantineCount());
    }

    [Fact]
    public async Task A_success_between_two_refusals_starts_the_count_over()
    {
        Queue("s", 1);
        fake.On("POST /api/agent/batches", HttpStatusCode.BadRequest, "{}");
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        fake.On("POST /api/agent/batches", body => (HttpStatusCode.OK, JsonSerializer.Serialize(new BatchAck(JsonSerializer.Deserialize<EventBatch>(body, ApiClient.Json)!.BatchSeq, false, 1), ApiClient.Json)));
        Assert.Equal(1, await relay.UploadAsync(CancellationToken.None));

        Queue("s", 1);
        fake.On("POST /api/agent/batches", HttpStatusCode.BadRequest, "{}");
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None)); // two refusals again, not the third
        Assert.Equal(1, store.OutboxCount());
        Assert.Equal(0, store.QuarantineCount());
    }

    [Fact]
    public async Task What_was_moved_aside_is_deleted_after_the_retention_and_not_before()
    {
        Queue("s", 1);
        RefuseEventZero(HttpStatusCode.BadRequest);
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        await relay.UploadAsync(CancellationToken.None);
        Assert.Equal(1, store.QuarantineCount());

        Assert.Equal(0, store.ForgetQuarantined(clock.GetUtcNow() - TimeSpan.FromDays(7)));
        Assert.Equal(1, store.QuarantineCount());
        clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(1, store.ForgetQuarantined(clock.GetUtcNow() - TimeSpan.FromDays(7)));
        Assert.Equal(0, store.QuarantineCount());
        Assert.Equal(1, store.Count(LocalStore.QuarantinedKey)); // the count is the total moved, not what is still held
    }

    private string Status() => string.Join('\n', PushStatus.Describe(home.Config, store, clock.GetUtcNow(), null, daemonRunning: false));

    [Fact]
    public async Task The_status_says_how_many_events_are_held_back_only_while_some_are()
    {
        Queue("s", 3);
        RefuseEventZero(HttpStatusCode.BadRequest);
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        Assert.DoesNotContain("Held back", Status(), StringComparison.Ordinal); // refused, but nothing moved aside yet

        await relay.UploadAsync(CancellationToken.None);
        Assert.Contains("Held back: 1 event(s) the server refused for good are kept here and not uploaded.", Status(), StringComparison.Ordinal);
        using (Strings.Scope("tr")) Assert.Contains("Geri tutulan: sunucunun kalıcı olarak reddettiği 1 olay", Status(), StringComparison.Ordinal);
        using (Strings.Scope("de")) Assert.Contains("Zurückgehalten: 1 Ereignis(se)", Status(), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromDays(8));
        store.ForgetQuarantined(clock.GetUtcNow() - TimeSpan.FromDays(7));
        Assert.DoesNotContain("Held back", Status(), StringComparison.Ordinal); // what is still held, not the total ever moved
    }

    [Fact]
    public async Task The_batch_size_the_settings_name_is_honoured_and_an_older_server_that_names_none_changes_nothing()
    {
        var workspace = TestWorkspace.Id(home.Config);
        fake.On("POST /api/agent/batches", body =>
        {
            var batch = JsonSerializer.Deserialize<EventBatch>(body, ApiClient.Json)!;
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new BatchAck(batch.BatchSeq, false, batch.Events.Count), ApiClient.Json));
        });

        fake.On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 4096, workspace));
        await relay.SettingsAsync(CancellationToken.None);
        Queue("s", 3);
        await relay.UploadAsync(CancellationToken.None);
        Assert.Equal([2, 1], BatchesSent().Select(b => b.Events.Count)); // no size told: the agent's own (two)

        fake.On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 4096, workspace, MaxBatchEvents: 1));
        await relay.SettingsAsync(CancellationToken.None);
        Queue("s", 3);
        await relay.UploadAsync(CancellationToken.None);
        Assert.Equal([1, 1, 1], BatchesSent().Skip(2).Select(b => b.Events.Count)); // told one: no batch of two is even tried
    }
}
