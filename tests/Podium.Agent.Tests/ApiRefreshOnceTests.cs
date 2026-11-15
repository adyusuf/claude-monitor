using System.Net;
using System.Net.Http.Json;
using Podium.Agent.Auth;
using Podium.Agent.Net;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>F-212: calls that were in flight on the same expired access token share one refresh instead of one each.</summary>
public sealed class ApiRefreshOnceTests : IDisposable
{
    private const int Callers = 5;
    private readonly TempHome home = new();
    private readonly RefreshServer server = new(Callers);
    private readonly HttpClient http;
    private readonly ApiClient api;

    public ApiRefreshOnceTests()
    {
        http = ApiClient.CreateHttp("https://aipod.invalid", server);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
    }

    public void Dispose()
    {
        api.Dispose();
        http.Dispose();
        home.Dispose();
    }

    [Fact]
    public async Task Parallel_calls_on_one_expired_access_token_refresh_it_once()
    {
        await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => api.HeartbeatAsync(CancellationToken.None)));

        Assert.Equal(1, server.Refreshes);
        Assert.Equal("access-2", Credentials.For(home.Config).Read(Credentials.Access));
        Assert.False(api.Disconnected);
    }

    /// <summary>Answers 401 to every call on access-1, but only once all callers are in flight on it, so none can start late on the new token.</summary>
    private sealed class RefreshServer(int callers) : HttpMessageHandler
    {
        private readonly TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        private int refreshes;

        public int Refreshes => Volatile.Read(ref refreshes);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/agent/token/refresh")
            {
                var n = Interlocked.Increment(ref refreshes);
                var now = DateTimeOffset.UtcNow;
                return Json(new TokenResponse("access-" + (n + 1), "refresh-" + (n + 1), now.AddMinutes(30), now.AddDays(30), Guid.NewGuid(), Guid.NewGuid()));
            }

            if (request.Headers.Authorization?.Parameter != "access-1") return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (Interlocked.Increment(ref arrived) == callers) allArrived.SetResult();
            await allArrived.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: ApiClient.Json) };
    }
}
