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
    public ConcurrentQueue<(string File, string[] Args)> Calls { get; } = new();

    /// <summary>What `version` prints and its exit code (the downloaded binary's own report).</summary>
    public string VersionOutput { get; set; } = "";

    public int VersionExit { get; set; }
    public int CodesignExit { get; set; }

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Calls.Enqueue((file, [.. args]));
        if (file == "codesign") return (CodesignExit, "");
        return args.Count == 1 && args[0] == "version" ? (VersionExit, VersionOutput) : (-1, "");
    }
}

/// <summary>
/// Stands in for the daemon: records Stop and Start in order and, like a daemon that came up healthy, can write the two
/// health markers into the LocalStore. An unhealthy one writes nothing.
/// </summary>
public sealed class FakeDaemon(AgentConfig config, TimeProvider clock, bool healthy = true, string? version = null) : IDaemonControl
{
    private readonly ConcurrentQueue<string> calls = new();

    public bool Running { get; set; } = true;
    public bool StopResult { get; set; } = true;

    /// <summary>The version a healthy daemon reports (the offered one).</summary>
    public string? Version { get; set; } = version;

    /// <summary>Runs inside Stop: lets a test make a daemon healthy only late.</summary>
    public Action<FakeDaemon>? OnStop { get; set; }

    public IReadOnlyList<string> Calls => [.. calls];
    public int Starts => calls.Count(c => c.StartsWith("start", StringComparison.Ordinal));

    public bool IsRunning() => Running;

    public Task<bool> StopAsync(TimeSpan wait, CancellationToken ct)
    {
        calls.Enqueue("stop");
        OnStop?.Invoke(this);
        if (StopResult) Running = false;
        return Task.FromResult(StopResult);
    }

    public bool Start(string binary)
    {
        calls.Enqueue("start:" + binary);
        Running = true;
        if (healthy) WriteHealthMarkers();
        return true;
    }

    public void WriteHealthMarkers()
    {
        using var store = new LocalStore(config.DatabasePath);
        store.Set(UpdateHealth.VersionKey, Version ?? AgentConfig.Version);
        store.Set(Relay.LastContactKey, clock.GetUtcNow().ToString("O"));
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

/// <summary>
/// One updating machine in a box: a throw-away home with the installed binary ("OLD"), a signing key whose public half is
/// built into the agent, a server that publishes signed offers and zips, and fakes for the process runner and the daemon.
/// </summary>
public sealed class UpdateKit : IDisposable
{
    public const string Server = "https://monitor.invalid";
    public const string Channel = "test";
    public static readonly byte[] OldBytes = Encoding.ASCII.GetBytes("OLD");

    private readonly TempHome home;
    private readonly HttpClient http;
    private readonly ApiClient client;

    public UpdateKit(Func<AgentConfig, AgentConfig>? tweak = null, TimeProvider? clock = null, bool healthy = true)
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        home = new TempHome(c =>
        {
            c = c with { UpdatePublicKey = PublicKey, UpdateChannel = Channel, UpdateOs = "macos", UpdateArch = "arm64", UpdateHealthWait = TimeSpan.FromSeconds(5) };
            return tweak is null ? c : tweak(c);
        });
        Clock = clock ?? new VirtualClock(DateTimeOffset.UtcNow);
        Config = home.Config;
        Api = new FakeApi();
        Host = new DownloadHost(Api);
        Runner = new FakeRunner();
        Daemon = new FakeDaemon(Config, Clock, healthy);
        Log = new AgentLog(Config, Clock);
        Credentials.For(Config).Write(Credentials.Access, "access-1");
        http = ApiClient.CreateHttp(Server, Host);
        client = new ApiClient(http, Credentials.For(Config), Config.ApiCallTimeout);
        Updater = new Updater(Config, client, http, Runner, Daemon, Log, Clock);
    }

    public ECDsa Key { get; }
    public string PublicKey { get; }
    public AgentConfig Config { get; }
    public TimeProvider Clock { get; }
    public FakeApi Api { get; }
    public DownloadHost Host { get; }
    public FakeRunner Runner { get; }
    public FakeDaemon Daemon { get; }
    public AgentLog Log { get; }
    public Updater Updater { get; }
    public string Dir => home.Dir;

    public string BinaryName => Path.GetFileName(Config.BinaryPath);
    public string Previous => BinarySwap.Previous(Config.BinaryPath);
    public string Staged => BinarySwap.Staged(Config.BinaryPath);
    public UpdateState State => UpdateState.Load(Config);

    /// <summary>A version above the running one, computed from it so a version bump never breaks a test.</summary>
    public static string Newer => Bump(1);

    /// <summary>A higher one still (to tell two offers apart).</summary>
    public static string Newest => Bump(2);

    /// <summary>A version below the running one.</summary>
    public static string Older
    {
        get
        {
            var v = Version.Parse(AgentConfig.Version);
            if (v.Build > 0) return $"{v.Major}.{v.Minor}.{v.Build - 1}";
            if (v.Minor > 0) return $"{v.Major}.{v.Minor - 1}.0";
            if (v.Major > 0) return $"{v.Major - 1}.0.0";
            throw new InvalidOperationException("the running version is 0.0.0: there is nothing older");
        }
    }

    private static string Bump(int by)
    {
        var v = Version.Parse(AgentConfig.Version);
        return $"{v.Major}.{v.Minor}.{v.Build + by}";
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The installed binary, as `cm-agent install` leaves it.</summary>
    public void InstallOld()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config.BinaryPath)!);
        File.WriteAllBytes(Config.BinaryPath, OldBytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Config.BinaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Connect() =>
        (Identity.Load(Config) with { Server = Server, AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(Config);

    /// <summary>A zip with the given entries (by default the one the binary's own name asks for).</summary>
    public byte[] Zip(byte[]? content = null, params string[] names)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names.Length == 0 ? [BinaryName] : names)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(content ?? Encoding.ASCII.GetBytes("NEW-BINARY"));
            }
        }

        return buffer.ToArray();
    }

    public static string Url(string file) => $"{Server}/downloads/{file}";

    /// <summary>Signs for this machine's OS and CPU, with this kit's key unless another is given.</summary>
    public string Sign(string channel, string version, string sha256, string minSupported, ECDsa? key = null) =>
        UpdateManifest.Sign(key ?? Key, UpdateManifest.Payload(channel, version, Config.UpdateOs, Config.UpdateArch, sha256, minSupported));

    /// <summary>A correctly signed offer for these zip bytes, not yet published.</summary>
    public UpdateOffer Offer(byte[] zip, string? version = null, string? channel = null, string minSupported = "0.0.1", string? file = null)
    {
        version ??= Newer;
        var sha = Sha256(zip);
        return new UpdateOffer(version, Url(file ?? $"cm-agent-{version}.zip"), sha, Sign(channel ?? Channel, version, sha, minSupported), minSupported,
            channel ?? Channel);
    }

    /// <summary>Makes the server answer GET /api/agent/latest with this offer and serve its zip.</summary>
    public UpdateOffer Publish(UpdateOffer offer, byte[]? zip = null)
    {
        Api.On("GET /api/agent/latest*", HttpStatusCode.OK, offer);
        if (zip is not null) Host.Files[new Uri(offer.Url).AbsolutePath] = zip;
        return offer;
    }

    /// <summary>The usual good case: a signed newer zip, published, the runner reporting its version.</summary>
    public UpdateOffer PublishGood(string? version = null, byte[]? zip = null)
    {
        version ??= Newer;
        zip ??= Zip();
        Runner.VersionOutput = version + "\n";
        Daemon.Version ??= version;
        return Publish(Offer(zip, version), zip);
    }

    /// <summary>Asserts nothing about the installation changed: the old binary, no .prev, no staged file, no update folder.</summary>
    public void AssertUntouched()
    {
        Assert.Equal(OldBytes, File.ReadAllBytes(Config.BinaryPath));
        Assert.False(File.Exists(Previous), ".prev must not exist");
        Assert.False(File.Exists(Staged), "the staged file must be gone");
        Assert.False(Directory.Exists(Config.UpdateDir), "the update folder must be gone");
    }

    public void Dispose()
    {
        client.Dispose();
        http.Dispose();
        Key.Dispose();
        home.Dispose();
    }
}
