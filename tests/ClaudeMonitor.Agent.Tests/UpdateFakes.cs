using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Records what the updater asked to run and answers from a script; nothing real is ever started.</summary>
public sealed class FakeRunner : IProcessRunner
{
    /// <summary>The program macOS's code-signature check runs (an absolute path: a name would be looked up on PATH).</summary>
    public const string Codesign = "/usr/bin/codesign";

    public ConcurrentQueue<(string File, string[] Args)> Calls { get; } = new();

    /// <summary>What `version` prints and its exit code (the downloaded binary's own report).</summary>
    public string VersionOutput { get; set; } = "";

    public int VersionExit { get; set; }
    public int CodesignExit { get; set; }

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Calls.Enqueue((file, [.. args]));
        if (file == Codesign) return (CodesignExit, "");
        return args.Count == 1 && args[0] == "version" ? (VersionExit, VersionOutput) : (-1, "");
    }
}

/// <summary>
/// Stands in for the daemon: records Stop, Kill and Start in order and, like a daemon that came up healthy, can write the two
/// health markers into the LocalStore. An unhealthy one writes nothing (or only what a test tells it to report).
/// </summary>
public sealed class FakeDaemon(AgentConfig config, TimeProvider clock, bool healthy = true, string? version = null) : IDaemonControl
{
    private readonly ConcurrentQueue<string> calls = new();
    private readonly ConcurrentQueue<bool> stopScript = new();

    public bool Running { get; set; } = true;
    public bool StopResult { get; set; } = true;

    /// <summary>What <see cref="Kill"/> answers; when true the hung daemon is gone afterwards (its lock is released), when false it still runs.</summary>
    public bool KillResult { get; set; } = true;

    /// <summary>The version a healthy daemon reports (the offered one).</summary>
    public string? Version { get; set; } = version;

    /// <summary>Runs inside Stop: lets a test make a daemon healthy only late.</summary>
    public Action<FakeDaemon>? OnStop { get; set; }

    /// <summary>Runs inside Start, after a healthy daemon's markers: lets a test make the new daemon say something else.</summary>
    public Action<FakeDaemon>? OnStart { get; set; }

    public IReadOnlyList<string> Calls => [.. calls];
    public int Starts => calls.Count(c => c.StartsWith("start", StringComparison.Ordinal));
    public int Kills => calls.Count(c => c == "kill");

    /// <summary>The next stops answer these, in order (then <see cref="StopResult"/>): "the first stop works, the rollback's does not".</summary>
    public void ScriptStops(params bool[] results)
    {
        foreach (var r in results) stopScript.Enqueue(r);
    }

    public bool IsRunning() => Running;

    public Task<bool> StopAsync(TimeSpan wait, CancellationToken ct)
    {
        calls.Enqueue("stop");
        OnStop?.Invoke(this);
        var stopped = stopScript.TryDequeue(out var scripted) ? scripted : StopResult;
        if (stopped) Running = false;
        return Task.FromResult(stopped);
    }

    public bool Kill()
    {
        calls.Enqueue("kill");
        if (KillResult) Running = false;
        return KillResult;
    }

    public bool Start(string binary)
    {
        calls.Enqueue("start:" + binary);
        Running = true;
        if (healthy) WriteHealthMarkers();
        OnStart?.Invoke(this);
        return true;
    }

    public void WriteHealthMarkers() => Report(Version ?? AgentConfig.Version, contact: true, error: null);

    /// <summary>What a daemon writes about itself: its version (null: none), a heartbeat the API answered now, and the last heartbeat's failure type.</summary>
    public void Report(string? version, bool contact, string? error)
    {
        using var store = new LocalStore(config.DatabasePath);
        if (version is not null) store.Set(UpdateHealth.VersionKey, version);
        if (contact) store.Set(Relay.LastContactKey, clock.GetUtcNow().ToString("O"));
        if (error is not null) store.Set(Relay.HeartbeatErrorKey, error);
    }
}

/// <summary>
/// The server as the updater sees it: /downloads/* answers with bytes (or a scripted failure, redirect or endless body), the
/// rest goes to a <see cref="FakeApi"/>.
/// </summary>
public sealed class DownloadHost(FakeApi api) : HttpMessageHandler
{
    private readonly HttpMessageInvoker inner = new(api, disposeHandler: false);

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentQueue<string> Downloads { get; } = new();

    /// <summary>The query string of every call that went to the API (FakeApi records only paths).</summary>
    public ConcurrentQueue<string> Queries { get; } = new();
    public HttpStatusCode? DownloadStatus { get; set; }

    /// <summary>When set, the response claims to come from this address (what a followed redirect looks like).</summary>
    public Uri? RedirectedTo { get; set; }

    /// <summary>When true the body carries no length, so only counting the bytes can stop an oversized download.</summary>
    public bool UnknownLength { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (!path.StartsWith("/downloads/", StringComparison.Ordinal))
        {
            Queries.Enqueue(request.RequestUri.Query);
            return await inner.SendAsync(request, ct);
        }

        Downloads.Enqueue(path);
        if (DownloadStatus is { } status) return new HttpResponseMessage(status);
        if (!Files.TryGetValue(path, out var bytes)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = UnknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes),
        };
        // A real handler (SocketsHttpHandler) sets the request that produced the response, which after a redirect is the last one.
        response.RequestMessage = RedirectedTo is { } to ? new HttpRequestMessage(HttpMethod.Get, to) : request;
        return response;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>Waits on a condition, never on a fixed sleep.</summary>
public static class Until
{
    public static async Task True(Func<bool> condition, int seconds = 20)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition())
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException("the condition did not become true");
            await Task.Delay(10, CancellationToken.None);
        }
    }
}
