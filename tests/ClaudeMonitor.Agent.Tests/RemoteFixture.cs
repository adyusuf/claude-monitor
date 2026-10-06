using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A local database, a fake API and a signed-in client over a throw-away home.</summary>
public sealed class RemoteFixture : IDisposable
{
    public RemoteFixture(Func<Agent.Config.AgentConfig, Agent.Config.AgentConfig>? tweak = null)
    {
        Home = new TempHome(tweak);
        Clock = new ManualClock(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        Store = new LocalStore(Home.Config.DatabasePath);
        Http = ApiClient.CreateHttp("https://monitor.invalid", Fake);
        var creds = Credentials.For(Home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        Api = new ApiClient(Http, creds, Home.Config.ApiCallTimeout);
    }

    public TempHome Home { get; }
    public ManualClock Clock { get; }
    public LocalStore Store { get; }
    public FakeApi Fake { get; } = new();
    public HttpClient Http { get; }
    public ApiClient Api { get; }

    public void Dispose()
    {
        Api.Dispose();
        Http.Dispose();
        Store.Dispose();
        Home.Dispose();
    }

    public static (HttpStatusCode, string) NoContent(string _) => (HttpStatusCode.NoContent, "");
}
