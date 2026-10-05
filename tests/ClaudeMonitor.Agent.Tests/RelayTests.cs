using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class RelayTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);
    private readonly LocalStore store;
    private readonly FakeApi fake = new();
    private readonly HttpClient http;
    private readonly ApiClient api;
    private readonly Relay relay;

    public RelayTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        http = ApiClient.CreateHttp("https://monitor.invalid", fake);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
        relay = new Relay(home.Config with { BatchEvents = 2 }, store, api, clock);
    }

    public void Dispose()
    {
        api.Dispose();
        http.Dispose();
        store.Dispose();
        home.Dispose();
    }

    private void Queue(string session, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var payload = JsonSerializer.SerializeToElement(new { i });
            store.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, session, "hook:Stop", clock.GetUtcNow(), payload), payload.GetRawText());
        }
    }

    [Fact]
    public async Task The_outbox_goes_up_in_numbered_batches_and_a_failed_batch_is_resent_as_it_was()
    {
        Queue("s", 3);
        var calls = 0;
        fake.On("POST /api/agent/batches", body =>
        {
            calls++;
            var batch = JsonSerializer.Deserialize<EventBatch>(body, ApiClient.Json)!;
            return calls == 2 ? (HttpStatusCode.BadGateway, "{}") : (HttpStatusCode.OK, JsonSerializer.Serialize(new BatchAck(batch.BatchSeq, false, batch.Events.Count), ApiClient.Json));
        });
        await Assert.ThrowsAsync<ApiException>(() => relay.UploadAsync(CancellationToken.None));
        Assert.Equal(1, store.OutboxCount());
        Assert.Equal(1, await relay.UploadAsync(CancellationToken.None));
        Assert.Equal(0, store.OutboxCount());
        var seqs = fake.Seen.Where(s => s.Path == "/api/agent/batches").Select(s => JsonSerializer.Deserialize<EventBatch>(s.Body, ApiClient.Json)!.BatchSeq).ToList();
        Assert.Equal([1L, 2L, 2L], seqs);
        Assert.All(fake.Seen, s => Assert.Equal("access-1", s.Auth));
    }

    [Fact]
    public async Task A_duplicate_ack_counts_nothing_and_an_expired_token_is_refreshed_once()
    {
        Queue("s", 1);
        var tries = 0;
        fake.On("POST /api/agent/batches", body => ++tries == 1
            ? (HttpStatusCode.Unauthorized, "{}")
            : (HttpStatusCode.OK, JsonSerializer.Serialize(new BatchAck(1, true, 0), ApiClient.Json)));
        fake.On("POST /api/agent/token/refresh", HttpStatusCode.OK,
            new TokenResponse("access-2", "refresh-2", clock.GetUtcNow().AddMinutes(30), clock.GetUtcNow().AddDays(30), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(0, await relay.UploadAsync(CancellationToken.None));
        Assert.Equal("access-2", fake.Seen.Last().Auth);
        Assert.Equal("refresh-2", Credentials.For(home.Config).Read(Credentials.Refresh));
        Assert.False(api.Disconnected);
    }

    [Fact]
    public async Task Only_an_answered_heartbeat_is_recorded_as_contact()
    {
        fake.On("POST /api/agent/heartbeat", HttpStatusCode.BadGateway, "{}");
        await Assert.ThrowsAsync<ApiException>(() => relay.HeartbeatAsync(CancellationToken.None));
        Assert.Null(store.Get(Relay.LastContactKey));

        fake.On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}");
        fake.On("POST /api/agent/token/refresh", HttpStatusCode.Unauthorized, "{}");
        await Assert.ThrowsAsync<ApiException>(() => relay.HeartbeatAsync(CancellationToken.None));
        Assert.Null(store.Get(Relay.LastContactKey));

        fake.On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "");
        await relay.HeartbeatAsync(CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), DateTimeOffset.Parse(store.Get(Relay.LastContactKey)!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_refused_refresh_disconnects_the_agent()
    {
        fake.On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}");
        fake.On("POST /api/agent/token/refresh", HttpStatusCode.Unauthorized, "{}");
        await Assert.ThrowsAsync<ApiException>(() => api.HeartbeatAsync(CancellationToken.None));
        Assert.True(api.Disconnected);
    }

    [Fact]
    public async Task Settings_are_kept_locally()
    {
        fake.On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(false, 4096, Guid.NewGuid()));
        await relay.SettingsAsync(CancellationToken.None);
        Assert.Equal("false", store.Get("settings.mask_secrets"));
        Assert.Equal("4096", store.Get("settings.event_max_bytes"));
    }

    [Fact]
    public async Task Permission_requests_reach_the_api_after_their_session_and_expire_locally_when_too_late()
    {
        Queue("s1", 1);
        fake.On("POST /api/agent/batches", HttpStatusCode.OK, new BatchAck(1, false, 1));
        var remote = Guid.NewGuid();
        fake.On("POST /api/agent/permission-requests", HttpStatusCode.OK, new PermissionRequestCreated(remote, clock.GetUtcNow().AddMinutes(2)));
        store.AddPermission(new PermissionAsk("l1", HarnessKinds.ClaudeCode, "s1", "Bash", "{\"command\":\"ls\"}", 60, clock.GetUtcNow(), null, null, null, "new"));
        store.AddPermission(new PermissionAsk("l2", HarnessKinds.ClaudeCode, "s1", "Bash", "{}", 60, clock.GetUtcNow().AddSeconds(-59), null, null, null, "new"));
        await relay.PermissionsAsync(CancellationToken.None);
        Assert.Equal(remote.ToString(), store.Permission("l1")!.RemoteId);
        Assert.Equal(PermissionStates.Expired, store.Permission("l2")!.State);
        Assert.True(fake.Count("POST /api/agent/batches") >= 1);

        store.AddPermission(new PermissionAsk("l3", HarnessKinds.ClaudeCode, "unknown", "Bash", "{}", 60, clock.GetUtcNow(), null, null, null, "new"));
        fake.On("POST /api/agent/permission-requests", HttpStatusCode.NotFound, "{}");
        await relay.PermissionsAsync(CancellationToken.None);
        Assert.Equal(PermissionStates.New, store.Permission("l3")!.State);
    }

    [Fact]
    public async Task Stream_messages_land_in_the_local_database()
    {
        fake.On("POST /api/agent/commands/*", HttpStatusCode.NoContent, "");
        var command = new AgentCommandMessage(Guid.NewGuid(), Guid.NewGuid(), "s1", CommandKinds.Prompt, "hello", clock.GetUtcNow().AddMinutes(5));
        Assert.True(await relay.OnStreamAsync(AgentStreamEvents.Command, JsonSerializer.SerializeToElement(command, ApiClient.Json), CancellationToken.None));
        Assert.Equal("hello", store.TakeCommand("s1", CommandKinds.Prompt, clock.GetUtcNow())!.Body);
        Assert.Contains(fake.Seen, s => s.Path.EndsWith("/status", StringComparison.Ordinal) && s.Body.Contains("delivered", StringComparison.Ordinal));

        store.AddPermission(new PermissionAsk("l1", HarnessKinds.ClaudeCode, "s1", "Bash", "{}", 60, clock.GetUtcNow(), null, null, null, "new"));
        var remote = Guid.NewGuid();
        store.PermissionSent("l1", remote.ToString());
        var answer = new PermissionAnswerMessage(remote, "s1", PermissionDecisions.Allow, null);
        Assert.True(await relay.OnStreamAsync(AgentStreamEvents.PermissionAnswer, JsonSerializer.SerializeToElement(answer, ApiClient.Json), CancellationToken.None));
        Assert.Equal(PermissionDecisions.Allow, store.Permission("l1")!.Decision);
        Assert.True(await relay.OnStreamAsync(AgentStreamEvents.Ping, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
        Assert.False(await relay.OnStreamAsync(AgentStreamEvents.Revoked, JsonSerializer.SerializeToElement(new { }), CancellationToken.None));

        await relay.ReportCommandsAsync(CancellationToken.None);
        Assert.Contains(fake.Seen, s => s.Body.Contains("applied", StringComparison.Ordinal));
        Assert.Empty(store.TakenCommands());
    }

    [Fact]
    public async Task The_stream_is_read_as_server_sent_events()
    {
        var id = Guid.NewGuid();
        fake.On("GET /api/agent/stream", HttpStatusCode.OK,
            $"event: ping\ndata: {{}}\n\nevent: command\ndata: {{\"id\":\"{id}\",\"sessionId\":\"{Guid.NewGuid()}\",\"sessionExternalId\":\"s\",\"kind\":\"stop\",\"expiresAt\":\"2030-01-01T00:00:00Z\"}}\n\n");
        var seen = new List<string>();
        await foreach (var (name, _) in api.StreamAsync(CancellationToken.None)) seen.Add(name);
        Assert.Equal(["ping", "command"], seen);
    }
}
