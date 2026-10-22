using Podium.Api.Config;
using Podium.Api.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>An answer made up on the spot, for the odd responses a real site gives (a broken body, no body, a hanging call).</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Seen { get; } = [];
    /// <summary>The body of each request, read when it was sent (the caller disposes the request after the call).</summary>
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return respond(request);
    }
}

/// <summary>
/// The Atlassian clients built by hand, without the API and without a database, over a handler the test chooses: the scripted
/// <see cref="FakeJira"/> by default. Used for the adapter's own tests.
/// </summary>
public sealed class JiraClients : IDisposable
{
    private readonly ServiceProvider provider;

    public JiraClients(Func<HttpMessageHandler>? handler = null, FakeJira? jira = null)
    {
        Jira = jira ?? new FakeJira(() => DateTimeOffset.UtcNow);
        Config = ApiConfig.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AIPOD_JIRA_CLIENT_ID"] = FakeJira.ClientId,
            ["AIPOD_JIRA_CLIENT_SECRET"] = FakeJira.ClientSecret,
        }).Build(), development: true);
        var services = new ServiceCollection();
        services.AddHttpClient(JiraApi.ClientName).ConfigurePrimaryHttpMessageHandler(handler ?? Jira.Handler);
        provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IHttpClientFactory>();
        Api = new JiraApi(clients, Config);
        OAuth = new JiraOAuth(clients, Config);
        Tracker = new JiraTracker(Api);
        Writer = new JiraWriter(Api, Tracker);
    }

    public FakeJira Jira { get; }
    public ApiConfig Config { get; }
    public JiraApi Api { get; }
    public JiraOAuth OAuth { get; }
    public JiraTracker Tracker { get; }
    public JiraWriter Writer { get; }

    /// <summary>A tracker context for the site that calls with the account's own (fake) access token.</summary>
    public TrackerContext Context(FakeSite site, FakeAccount account)
    {
        var token = Jira.AccessFor(account);
        return new TrackerContext(site.CloudId, _ => Task.FromResult(token));
    }

    public void Dispose() => provider.Dispose();
}
