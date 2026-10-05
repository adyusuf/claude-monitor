using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// A daemon call on a dead connection gives up at its own deadline, as a failure to retry; the daemon's stop is still a
/// cancellation, and the stream keeps no deadline.
/// </summary>
public sealed class ApiDeadlineTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(100);
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    public static TheoryData<string> Calls => ["batch", "heartbeat", "settings", "command-status", "permission"];

    private static Task Call(ApiClient api, string call, CancellationToken ct) => call switch
    {
        "batch" => api.SendBatchAsync(new EventBatch(1, []), ct),
        "heartbeat" => api.HeartbeatAsync(ct),
        "settings" => api.SettingsAsync(ct),
        "command-status" => api.CommandStatusAsync(Guid.NewGuid().ToString(), CommandStatuses.Applied, null, ct),
        "permission" => api.CreatePermissionAsync(
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, "s", "Bash", JsonSerializer.SerializeToElement(new { }), 60), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call, null),
    };

    private (HttpClient Http, ApiClient Api) Client(Scripted handler, TimeSpan deadline)
    {
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "a");
        creds.Write(Credentials.Refresh, "r");
        var http = ApiClient.CreateHttp("https://m.invalid", handler);
        return (http, new ApiClient(http, creds, deadline));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task A_call_on_a_hanging_connection_gives_up_at_its_deadline_as_a_timeout(string call)
    {
        var handler = new Scripted((_, ct) => Hang(ct));
        var (http, api) = Client(handler, Deadline);
        using (http)
        using (api)
        {
            var watch = Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<TimeoutException>(() => Call(api, call, CancellationToken.None));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
            Assert.IsAssignableFrom<OperationCanceledException>(e.InnerException);
            Assert.Equal("the API did not answer within 0.1 s", e.Message);
            Assert.Equal(1, handler.Calls);
        }
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task The_daemons_stop_stays_a_cancellation_while_a_call_hangs(string call)
    {
        var (http, api) = Client(new Scripted((_, ct) => Hang(ct)), TimeSpan.FromMinutes(5));
        using (http)
        using (api)
        {
            using var stop = new CancellationTokenSource(Deadline);
            var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(api, call, stop.Token));
            Assert.IsNotType<TimeoutException>(e.InnerException);
        }
    }

    [Fact]
    public async Task A_token_refresh_that_hangs_falls_under_the_same_deadline()
    {
        var handler = new Scripted((request, ct) => request.RequestUri!.AbsolutePath == "/api/agent/token/refresh"
            ? Hang(ct)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }));
        var (http, api) = Client(handler, Deadline);
        using (http)
        using (api)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => api.HeartbeatAsync(CancellationToken.None));
            Assert.Equal(2, handler.Calls); // the heartbeat, then the refresh that hung
            Assert.False(api.Disconnected);
        }
    }

    [Fact]
    public async Task The_stream_keeps_no_deadline()
    {
        var handler = new Scripted(async (_, ct) =>
        {
            await Task.Delay(Deadline * 4, ct);
            return Answer(HttpStatusCode.OK, "event: revoked\ndata: {}\n\n");
        });
        var (http, api) = Client(handler, Deadline);
        using (http)
        using (api)
        {
            var events = new List<string>();
            await foreach (var (name, _) in api.StreamAsync(CancellationToken.None)) events.Add(name);
            Assert.Equal([AgentStreamEvents.Revoked], events);
        }
    }

    [Fact]
    public async Task A_timed_out_heartbeat_is_logged_and_retried_and_the_loop_keeps_running()
    {
        using var daemonHome = new TempHome(c => c with
        {
            ApiCallTimeout = Deadline,
            HeartbeatEvery = TimeSpan.FromMilliseconds(20),
            RetryMax = TimeSpan.FromMilliseconds(50),
        });
        var beats = 0;
        var handler = new Scripted(async (request, ct) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/agent/heartbeat":
                    if (Interlocked.Increment(ref beats) == 1) return await Hang(ct);
                    return Answer(HttpStatusCode.NoContent, "");
                case "/api/agent/stream":
                    // Revokes only once a heartbeat after the timed-out one has been answered.
                    while (Volatile.Read(ref beats) < 3) await Task.Delay(10, ct);
                    return Answer(HttpStatusCode.OK, "event: revoked\ndata: {}\n\n");
                case "/api/agent/settings":
                    return Answer(HttpStatusCode.OK, JsonSerializer.Serialize(new AgentSettings(true, 1000, Guid.NewGuid()), ApiClient.Json));
                default:
                    return Answer(HttpStatusCode.NotFound, "{}");
            }
        });
        (Identity.Load(daemonHome.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid() }).Save(daemonHome.Config);
        Credentials.For(daemonHome.Config).Write(Credentials.Access, "a");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var log = new AgentLog(daemonHome.Config, TimeProvider.System);
        Assert.Equal(0, await new DaemonHost(daemonHome.Config, TimeProvider.System, log, handler).RunAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(Volatile.Read(ref beats) >= 3, $"{beats} heartbeats");
        var said = await File.ReadAllTextAsync(daemonHome.Config.LogPath);
        Assert.Contains("heartbeat failed (1): TimeoutException the API did not answer within 0.1 s", said, StringComparison.Ordinal);
        Assert.Contains("revoked from the web", said, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A connection that never answers: it ends only when the request's token is cancelled.</summary>
    private static async Task<HttpResponseMessage> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new UnreachableException();
    }

    private sealed class Scripted(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return answer(request, ct);
        }
    }
}
