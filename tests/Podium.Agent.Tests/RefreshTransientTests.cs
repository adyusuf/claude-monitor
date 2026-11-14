using System.Net;
using Podium.Agent.Auth;
using Podium.Agent.Daemon;
using Podium.Agent.Net;

namespace Podium.Agent.Tests;

/// <summary>
/// Only a 401 from the token refresh means the agent's credentials are refused for good. A 429, a 5xx or a proxy's
/// answer is passing: the call fails, the daemon retries later, and the agent keeps its identity and its tokens.
/// </summary>
public sealed class RefreshTransientTests
{
    public static TheoryData<HttpStatusCode> Passing =>
        [HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable];

    [Theory]
    [MemberData(nameof(Passing))]
    public async Task A_refresh_answered_429_or_5xx_fails_the_call_but_does_not_disconnect(HttpStatusCode status)
    {
        using var home = new TempHome();
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}")
            .On("POST /api/agent/token/refresh", status, "{}");
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "a");
        creds.Write(Credentials.Refresh, "r");
        using var http = ApiClient.CreateHttp("https://m.invalid", fake);
        using var api = new ApiClient(http, creds, TimeSpan.FromSeconds(30));

        var e = await Assert.ThrowsAsync<ApiException>(() => api.HeartbeatAsync(CancellationToken.None));

        Assert.Equal(status, e.Status);
        Assert.False(api.Disconnected);
        Assert.Null(api.DisconnectReason);
        Assert.Equal("r", creds.Read(Credentials.Refresh)); // the same token is sent again on the next try
        Assert.Equal(1, fake.Count("POST /api/agent/token/refresh"));
    }

    [Fact]
    public async Task A_daemon_whose_refresh_answers_503_keeps_its_identity_and_retries()
    {
        using var home = new TempHome(c => c with { FlushEvery = TimeSpan.FromMilliseconds(20) });
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}")
            .On("POST /api/agent/token/refresh", HttpStatusCode.ServiceUnavailable, "{}")
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(200);
                return (HttpStatusCode.Unauthorized, "{}");
            });
        var agentId = Guid.NewGuid();
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = agentId }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
        Credentials.For(home.Config).Write(Credentials.Refresh, "r");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake).RunAsync(stop.Token);

        while (fake.Count("POST /api/agent/token/refresh") < 3 && !run.IsCompleted && !stop.IsCancellationRequested) await Task.Delay(20);
        var stillRunning = !run.IsCompleted;
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(stillRunning, "the daemon stopped on a passing refresh failure");
        Assert.True(fake.Count("POST /api/agent/token/refresh") >= 3);
        var identity = Identity.Load(home.Config);
        Assert.True(identity.Connected);
        Assert.Equal(agentId, identity.AgentId);
        Assert.Equal("r", Credentials.For(home.Config).Read(Credentials.Refresh));
    }
}
