using System.Text;
using ClaudeMonitor.Api.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>What Jira's webhook delivery looks like from outside: a signed call with no session, and the queue behind it.</summary>
public static class WebhookKit
{
    public static string Token(ApiFactory api, string secret = FakeJira.ClientSecret, TimeSpan? validFor = null) =>
        "Bearer " + WebhookJwt.Sign($"{{\"iss\":\"jira\",\"exp\":{(api.Clock.GetUtcNow() + (validFor ?? TimeSpan.FromHours(1))).ToUnixTimeSeconds()}}}", secret);

    /// <summary>One delivery from a caller that has no cookies; the token is a valid one unless the test passes another (or none, with <c>""</c>).</summary>
    public static async Task<HttpResponseMessage> DeliverAsync(ApiFactory api, Guid connection, string body, string? id = null, string? authorization = null)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integrations/jira/webhook/{connection}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var auth = authorization ?? Token(api);
        if (auth.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", auth);
        if (id is not null) request.Headers.Add("X-Atlassian-Webhook-Identifier", id);
        return await client.SendAsync(request);
    }

    public static string IssueEvent(string key, string kind = "jira:issue_updated") => $"{{\"webhookEvent\":\"{kind}\",\"issue\":{{\"key\":\"{key}\"}}}}";

    public static string SprintEvent(string id, string kind = "sprint_updated") => $"{{\"webhookEvent\":\"{kind}\",\"sprint\":{{\"id\":{id}}}}}";

    /// <summary>The next queued request of this connection (others' are passed over), waiting up to ten seconds.</summary>
    public static async Task<SyncRequest> NextAsync(ApiFactory api, Guid connection)
    {
        var inbox = api.Services.GetRequiredService<SyncInbox>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var request = await inbox.TakeAsync(cts.Token);
            if (request.ConnectionId == connection) return request;
        }
    }
}

/// <summary>The hosted worker of the API, started by hand for a test (background jobs are off in the test host) and stopped at the end.</summary>
public sealed class RunningWorker : IAsyncDisposable
{
    private readonly SyncWorker worker;

    private RunningWorker(SyncWorker worker) => this.worker = worker;

    public static async Task<RunningWorker> StartAsync(ApiFactory api)
    {
        var worker = new SyncWorker(api.Services.GetRequiredService<IServiceScopeFactory>(), api.Services.GetRequiredService<SyncInbox>(),
            api.Services.GetRequiredService<ILogger<SyncWorker>>());
        await worker.StartAsync(CancellationToken.None);
        return new RunningWorker(worker);
    }

    public async ValueTask DisposeAsync()
    {
        await worker.StopAsync(CancellationToken.None);
        var task = worker.ExecuteTask;
        worker.Dispose();
        if (task is { IsFaulted: true }) await task; // a worker that died on a request is a failure of the test, not a silent end
    }
}
