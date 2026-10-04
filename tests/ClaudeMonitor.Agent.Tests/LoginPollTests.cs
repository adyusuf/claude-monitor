using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The login's poll survives a flaky connection: a transport failure is no answer, not the end of the login.</summary>
public sealed class LoginPollTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(body as string ?? JsonSerializer.Serialize(body, ApiClient.Json), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Code(int expiresIn = 600) =>
        Answer(HttpStatusCode.OK, new DeviceCodeResponse("dev", "BCDF-GHJK", "https://m.invalid/device", 0, expiresIn));

    private HttpResponseMessage Tokens() =>
        Answer(HttpStatusCode.OK, new TokenResponse("a", "r", clock.GetUtcNow(), clock.GetUtcNow(), Guid.NewGuid(), Guid.NewGuid()));

    private Task<int> Run(AgentConfig config, Scripted api, StringWriter output, CancellationToken ct = default) =>
        new Login(config, output, new FastClock(clock), _ => true, "macos").RunAsync("https://m.invalid", api, ct);

    [Fact]
    public async Task Transport_failures_are_polled_through_with_a_backoff_until_the_tokens_arrive()
    {
        Exception[] failures =
        [
            new HttpRequestException("Can't assign requested address", new SocketException(49)),
            new HttpRequestException("transport", new IOException("Unable to read data from the transport connection")),
            new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException()),
        ];
        var api = new Scripted(Code(), (poll, _) => poll <= failures.Length ? throw failures[poll - 1] : Task.FromResult(Tokens()));
        var output = new StringWriter();

        Assert.Equal(0, await Run(home.Config, api, output));
        var said = output.ToString();
        Assert.Equal(4, api.Polls);
        Assert.Contains("HttpRequestException: Can't assign requested address); trying again in 5 s.", said, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException: transport); trying again in 10 s.", said, StringComparison.Ordinal);
        Assert.Contains("TaskCanceledException: HttpClient.Timeout elapsed); trying again in 15 s.", said, StringComparison.Ordinal);
        Assert.Contains("Connected.", said, StringComparison.Ordinal);
        Assert.Equal("r", Credentials.For(home.Config).Read(Credentials.Refresh));
    }

    [Fact]
    public async Task A_server_that_never_answers_ends_in_a_clean_expiry()
    {
        var api = new Scripted(Code(expiresIn: 30), (_, _) => throw new HttpRequestException("Operation timed out", new IOException()));
        var output = new StringWriter();

        Assert.Equal(1, await Run(home.Config, api, output));
        var said = output.ToString();
        Assert.Contains("The code expired while the server did not answer (HttpRequestException: Operation timed out)", said, StringComparison.Ordinal);
        Assert.True(api.Polls > 1, $"polled {api.Polls} times");
        Assert.False(Identity.Load(home.Config).Connected);
    }

    [Fact]
    public async Task The_backoff_stops_at_the_ceiling_and_a_gateway_error_counts_as_no_answer()
    {
        using var capped = new TempHome(c => c with { LoginPollMax = TimeSpan.FromSeconds(12) });
        var api = new Scripted(Code(), (poll, _) => Task.FromResult(poll <= 4 ? Answer(HttpStatusCode.BadGateway, "<html>bad gateway</html>") : Tokens()));
        var output = new StringWriter();

        Assert.Equal(0, await Run(capped.Config, api, output));
        var lines = output.ToString().Split(Environment.NewLine).Where(l => l.StartsWith("The server did not answer", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["(it answered 502); trying again in 5 s.", "(it answered 502); trying again in 10 s.",
             "(it answered 502); trying again in 12 s.", "(it answered 502); trying again in 12 s."],
            lines.Select(l => l["The server did not answer ".Length..]));
    }

    [Fact]
    public async Task A_poll_that_hangs_is_cut_off_by_the_request_timeout()
    {
        using var quick = new TempHome(c => c with { LoginRequestTimeout = TimeSpan.FromMilliseconds(200) });
        var api = new Scripted(Code(), async (poll, ct) =>
        {
            if (poll == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a dead keep-alive connection: no bytes, ever
            return Tokens();
        });
        var output = new StringWriter();

        Assert.Equal(0, await Run(quick.Config, api, output).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("TaskCanceledException", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, api.Polls);
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_taken_for_a_transport_failure()
    {
        using var stop = new CancellationTokenSource();
        var api = new Scripted(Code(), async (_, _) =>
        {
            await stop.CancelAsync();
            throw new OperationCanceledException(stop.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(home.Config, api, new StringWriter(), stop.Token));
        Assert.Equal(1, api.Polls);
    }

    [Fact]
    public async Task A_first_request_that_cannot_reach_the_server_says_so_and_exits()
    {
        var api = new Scripted(null, (_, _) => Task.FromResult(Tokens()));
        var output = new StringWriter();

        Assert.Equal(1, await Run(home.Config, api, output));
        Assert.Contains("The server did not answer (HttpRequestException: No route to host)", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, api.Polls);
    }

    [Fact]
    public void Pooled_connections_are_retired_and_connects_time_out()
    {
        using var handler = ApiClient.CreateHandler();
        Assert.Equal(AgentConfig.ConnectionLifetime, handler.PooledConnectionLifetime);
        Assert.Equal(AgentConfig.ConnectionIdle, handler.PooledConnectionIdleTimeout);
        Assert.Equal(AgentConfig.ConnectTimeout, handler.ConnectTimeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, handler.PooledConnectionLifetime);

        using var timed = ApiClient.CreateHttp("https://m.invalid", handler, TimeSpan.FromSeconds(7));
        using var open = ApiClient.CreateHttp("https://m.invalid", handler);
        Assert.Equal((TimeSpan.FromSeconds(7), Timeout.InfiniteTimeSpan), (timed.Timeout, open.Timeout));
    }

    /// <summary>The device-code answer (null: the network fails), then each poll scripted by its number.</summary>
    private sealed class Scripted(HttpResponseMessage? code, Func<int, CancellationToken, Task<HttpResponseMessage>> poll) : HttpMessageHandler
    {
        private int polls;

        public int Polls => polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath switch
            {
                "/api/device/code" => code is null ? throw new HttpRequestException("No route to host") : Task.FromResult(code),
                "/api/device/token" => poll(Interlocked.Increment(ref polls), ct),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            };
    }
}
