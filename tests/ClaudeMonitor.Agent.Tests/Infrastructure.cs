using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A throw-away agent home (never the user's), with tokens in files instead of the keychain.</summary>
public sealed class TempHome : IDisposable
{
    public TempHome(Func<AgentConfig, AgentConfig>? tweak = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "cm-agent-test-" + Guid.NewGuid().ToString("N"));
        var config = AgentConfig.FromEnvironment(key => key switch
        {
            "CM_AGENT_HOME" => Dir,
            "CM_CREDENTIALS" => "file",
            _ => null,
        }) with
        { PollEvery = TimeSpan.FromMilliseconds(10), PermissionWait = TimeSpan.FromSeconds(2) };
        Config = tweak is null ? config : tweak(config);
        Config.EnsureHome();
    }

    public string Dir { get; }
    public AgentConfig Config { get; }

    public void Dispose()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>
/// A clock whose timers fire at once and move time forward by their due time, so the time a wait would have taken is
/// measured exactly and costs nothing.
/// </summary>
public sealed class VirtualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly DateTimeOffset origin = start;
    private DateTimeOffset now = start;
    private int timers;

    public TimeSpan Elapsed => GetUtcNow() - origin;

    public void Advance(TimeSpan by)
    {
        lock (gate) now += by;
    }

    public int Timers => Volatile.Read(ref timers);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate) now += dueTime;
        Interlocked.Increment(ref timers);
        // Not inline: Task.Delay keeps the timer only after this returns, and its callback needs it.
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Answers the agent's HTTP calls from a table of routes and records every request it saw.</summary>
public sealed class FakeApi : HttpMessageHandler
{
    public ConcurrentQueue<(string Method, string Path, string Body, string? Auth)> Seen { get; } = new();
    public Dictionary<string, Func<string, (HttpStatusCode, string)>> Routes { get; } = new();

    public FakeApi On(string methodAndPath, HttpStatusCode status, object body)
    {
        Routes[methodAndPath] = _ => (status, body as string ?? JsonSerializer.Serialize(body, Net.ApiClient.Json));
        return this;
    }

    public FakeApi On(string methodAndPath, Func<string, (HttpStatusCode, string)> answer)
    {
        Routes[methodAndPath] = answer;
        return this;
    }

    public int Count(string methodAndPath) => Seen.Count(s => $"{s.Method} {s.Path}" == methodAndPath);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        Seen.Enqueue((request.Method.Method, request.RequestUri.AbsolutePath, body, request.Headers.Authorization?.Parameter));
        var match = Routes.FirstOrDefault(r => r.Key == key || (r.Key.EndsWith('*') && key.StartsWith(r.Key[..^1], StringComparison.Ordinal)));
        var (status, text) = match.Value is null ? (HttpStatusCode.NotFound, "{}") : match.Value(body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
