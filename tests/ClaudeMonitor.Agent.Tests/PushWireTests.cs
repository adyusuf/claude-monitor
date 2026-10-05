using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The stream from the web, its recorded state, the transcript confirmation, the status text and the switch (ADR-0003).</summary>
public sealed class PushWireTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private void Connect() =>
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);

    /// <summary>A response whose body never ends: a stream the server has stopped pinging.</summary>
    private sealed class SilentStream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) });
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stream_the_server_stopped_pinging_fails_as_a_timeout_not_as_a_shutdown()
    {
        using var http = ApiClient.CreateHttp("https://m.invalid", new SilentStream());
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "a");
        using var api = new ApiClient(http, creds, TimeSpan.FromSeconds(5)) { StreamIdleTimeout = TimeSpan.FromMilliseconds(150) };
        var opened = 0;
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in api.StreamAsync(CancellationToken.None, () => opened++)) { }
        });
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task The_daemon_records_the_stream_the_last_message_and_a_failed_upload_without_any_content()
    {
        Connect();
        Credentials.For(home.Config).Write(Credentials.Access, "a");
        var streams = 0;
        var id = Guid.NewGuid();
        var message = new AgentCommandMessage(id, Guid.NewGuid(), "s-1", CommandKinds.Prompt, "SECRET BODY", DateTimeOffset.UtcNow.AddMinutes(5));
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "")
            .On("POST /api/agent/batches", HttpStatusCode.InternalServerError, "{}")
            .On("POST /api/agent/commands/*", HttpStatusCode.NoContent, "")
            .On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid()))
            .On("GET /api/agent/stream", _ => Interlocked.Increment(ref streams) == 1
                ? throw new HttpRequestException("network down")
                : (HttpStatusCode.OK, $"event: command\ndata: {JsonSerializer.Serialize(message, ApiClient.Json)}\n\nevent: revoked\ndata: {{}}\n\n"));
        using (var seed = new LocalStore(home.Config.DatabasePath))
        {
            var payload = JsonSerializer.SerializeToElement(new { x = 1 });
            seed.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, "s-1", "hook:Stop", DateTimeOffset.UtcNow, payload), payload.GetRawText());
        }

        var config = home.Config with { FlushEvery = TimeSpan.FromMilliseconds(20), RetryMax = TimeSpan.FromMilliseconds(50) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await new DaemonHost(config, TimeProvider.System, new AgentLog(config, TimeProvider.System), fake).RunAsync(timeout.Token);

        using var store = new LocalStore(config.DatabasePath);
        Assert.Equal(id.ToString(), store.Get(PushStatus.StreamCommandKey));
        Assert.Equal(PushStatus.Connected, store.Get(PushStatus.StreamStateKey)); // it reconnected after the failure
        Assert.Equal("ApiException", store.Get(PushStatus.UploadErrorKey)); // the API answered 500: its type name, never its body
        Assert.Equal(1, store.OutboxCount()); // the event is still waiting, and now it says why
        var all = string.Join('|', store.Get(PushStatus.StreamErrorKey), store.Get(PushStatus.UploadErrorKey), await File.ReadAllTextAsync(config.LogPath));
        Assert.DoesNotContain("SECRET BODY", all, StringComparison.Ordinal);
    }

    [Fact]
    public void The_relay_notes_when_it_uploaded_and_when_the_stream_is_reconnecting()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        using var http = ApiClient.CreateHttp("https://m.invalid", new FakeApi());
        using var api = new ApiClient(http, Credentials.For(home.Config), TimeSpan.FromSeconds(5));
        var clock = new ManualClock(Start);
        var relay = new Relay(home.Config, store, api, clock);
        relay.StreamState(PushStatus.Reconnecting, 3, "TimeoutException");
        var lines = string.Join('\n', PushStatus.Describe(home.Config, store, clock.GetUtcNow(), null, daemonRunning: false));
        Assert.Contains("the agent daemon is not running", lines, StringComparison.Ordinal);
        Assert.False(PushStatus.DaemonRunning(home.Config)); // nobody holds the lock here
        using (DaemonHost.TryLock(home.Config.LockPath)) Assert.True(PushStatus.DaemonRunning(home.Config));
        Assert.Equal("3", store.Get(PushStatus.StreamFailuresKey));
        relay.UploadOutcome(null);
        Assert.Equal(Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture), store.Get(PushStatus.UploadAtKey));
    }

    [Fact]
    public void The_status_says_what_waits_how_long_and_whether_the_push_is_on()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var payload = JsonSerializer.SerializeToElement(new { x = 1 });
        store.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, "s", "hook:Stop", Start.AddSeconds(-40), payload), payload.GetRawText());
        store.Set(PushStatus.StreamStateKey, PushStatus.Connected);
        store.Set(PushStatus.StreamStateAtKey, Start.AddMinutes(-5).ToString("O"));
        store.Set(PushStatus.StreamCommandKey, "cmd-9");
        store.Set(PushStatus.StreamCommandAtKey, Start.AddSeconds(-10).ToString("O"));
        var off = string.Join(' ', PushStatus.Describe(home.Config, store, Start, "s-1", daemonRunning: true));
        Assert.Contains("Events queued locally: 1 (oldest 40 s); last upload never.", off, StringComparison.Ordinal);
        Assert.Contains("Web stream: connected since 11:55:00Z; last message id cmd-9 (10 s ago).", off, StringComparison.Ordinal);
        Assert.Contains("Push into the session: off", off, StringComparison.Ordinal);

        store.SaveCommand(new LocalCommand("w-1", "s-1", CommandKinds.Prompt, "x", Start.AddMinutes(9)));
        var on = string.Join(' ', PushStatus.Describe(home.Config with { PushEnabled = true }, store, Start, "s-1", daemonRunning: true));
        Assert.Contains("on for this session (s-1)", on, StringComparison.Ordinal);
        Assert.Contains("nothing pushed yet", on, StringComparison.Ordinal);
        Assert.Contains("1 waiting for a hook", on, StringComparison.Ordinal);
    }

    // ---- the transcript confirms a push -----------------------------------------------------------------------------

    [Fact]
    public void A_push_is_confirmed_by_its_wrapper_in_the_transcript_and_not_by_a_bare_id()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.SaveCommand(new LocalCommand("c-1", "s-1", CommandKinds.Prompt, "x", Start.AddHours(1)));
        store.SaveCommand(new LocalCommand("c-2", "s-1", CommandKinds.Prompt, "y", Start.AddHours(1)));
        store.TakePromptsForPush("s-1", CommandKinds.Prompt, Start);
        var transcript = Path.Combine(home.Dir, "t.jsonl");
        File.WriteAllLines(transcript,
        [
            """{"type":"assistant","message":{"content":[{"type":"text","text":"last pushed c-1 at 12:00"}]}}""", // monitor_status output
            """{"type":"user","message":{"content":"<channel source=\"claude-monitor\" message_id=\"c-2\">\n<<<claude-monitor-message id=c-2\ny\nclaude-monitor-message>>>\n</channel>"}}""",
        ]);
        store.TrackTranscript(new TranscriptCursor("s-1", HarnessKinds.ClaudeCode, transcript, 0, null, null, null), Start);
        new TranscriptTailer(home.Config, store, new ManualClock(Start)).RunOnce();
        Assert.Equal(["c-2"], store.TakenCommands());
        Assert.Equal("c-1", Assert.Single(store.UnconfirmedPushes()).Id);
    }

    // ---- the switch -------------------------------------------------------------------------------------------------

    [Fact]
    public void Push_is_off_by_default_saved_by_install_and_overridden_by_the_environment()
    {
        Assert.False(home.Config.PushEnabled);
        var saved = SavedSettings.SavePush(home.Config, true);
        Assert.True(saved.PushEnabled);
        Assert.True(SavedSettings.Apply(home.Config).PushEnabled);
        var forcedOff = AgentConfig.FromEnvironment(k => k switch { "CM_AGENT_HOME" => home.Dir, "CM_PUSH" => "off", _ => null });
        Assert.False(SavedSettings.Apply(forcedOff).PushEnabled); // CM_PUSH wins over the saved value
        var scope = AgentConfig.FromEnvironment(k => k switch { "CM_PUSH_SCOPE" => "machine", "CM_PUSH" => "on", _ => null });
        Assert.Equal((PushScopes.Machine, true), (scope.PushScope, scope.PushEnabled));
        Assert.Equal(PushScopes.Session, AgentConfig.FromEnvironment(_ => null).PushScope);
    }

    [Fact]
    public void Install_push_on_adds_the_channel_to_the_plugin_and_says_how_to_start_claude_off_removes_it()
    {
        var source = Path.Combine(home.Dir, "cm-agent-src");
        File.WriteAllText(source, "binary");
        string Manifest() => File.ReadAllText(Path.Combine(home.Config.PluginDir, "monitor-agent", ".claude-plugin", "plugin.json"));
        (int Code, string Out, string Err) Install(params string[] extra)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            return (Cli.Install(["install", .. extra], home.Config, o, e, source, (_, _) => 0), o.ToString(), e.ToString());
        }

        var plain = Install();
        Assert.Equal(0, plain.Code);
        Assert.DoesNotContain("channels", Manifest(), StringComparison.Ordinal); // additive: the plugin is what it was
        Assert.Contains("next prompt", plain.Out, StringComparison.Ordinal);

        var on = Install("--push", "on");
        Assert.Contains("--dangerously-load-development-channels plugin:monitor-agent@monitor-agent-local", on.Out, StringComparison.Ordinal);
        var channel = JsonNode.Parse(Manifest())!["channels"]![0]!;
        Assert.Equal(("claude-monitor", "Claude Monitor"), (channel["server"]!.GetValue<string>(), channel["displayName"]!.GetValue<string>()));
        Assert.Equal(["claude-monitor"], JsonNode.Parse(File.ReadAllText(Path.Combine(home.Config.PluginDir, "monitor-agent", ".mcp.json")))!["mcpServers"]!.AsObject().Select(p => p.Key));

        Assert.Equal(0, Install().Code); // a later plain install keeps the choice
        Assert.Contains("channels", Manifest(), StringComparison.Ordinal);
        Assert.Equal(0, Install("--push", "off").Code);
        Assert.DoesNotContain("channels", Manifest(), StringComparison.Ordinal);
        Assert.Equal(2, Install("--push", "maybe").Code);
        Assert.Contains("--push takes on or off", Install("--push").Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_prints_the_push_account_and_the_hook_timeouts_do_not_change()
    {
        Connect();
        var o = new StringWriter();
        Assert.Equal(0, await Cli.StatusAsync(home.Config, o, new ManualClock(Start)));
        Assert.Contains("Push into the session: off", o.ToString(), StringComparison.Ordinal);
        Assert.Contains("Events queued locally: 0", o.ToString(), StringComparison.Ordinal);
        Assert.Equal(10, PluginInstaller.Hooks.First(h => h.Event == "UserPromptSubmit").Timeout);
    }
}
