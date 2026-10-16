using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>What asks need around the pump (ADR-0008 §3f): the session's permission mode from its hooks, the stream events, the transcript confirmation and the capability.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class AskWiringTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new(c => c with { PushEnabled = true, PushTransport = PushTransports.Inbox, MessagingSocket = "/tmp/cm-ask-test.sock" });
    private readonly ManualClock clock = new(Start);
    private readonly LocalStore store;

    public AskWiringTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private static string Id(int n) => $"0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e{n:00}";

    private async Task HookAsync(string hook, string session, string? mode)
    {
        var payload = new Dictionary<string, object?> { ["session_id"] = session, ["cwd"] = home.Dir };
        if (mode is not null) payload["permission_mode"] = mode;
        await new HookRunner(home.Config, store, clock).RunAsync(hook, JsonSerializer.Serialize(payload), CancellationToken.None);
    }

    [Fact]
    public async Task The_hooks_record_the_sessions_permission_mode_and_refresh_it_and_a_hook_without_a_mode_keeps_the_last_one()
    {
        await HookAsync("SessionStart", "s-1", "default");
        Assert.Equal("default", store.InboxOf("s-1")!.PermissionMode);

        await HookAsync("UserPromptSubmit", "s-1", "bypassPermissions");
        Assert.Equal("bypassPermissions", store.InboxOf("s-1")!.PermissionMode);

        await HookAsync("PreToolUse", "s-1", null);
        Assert.Equal("bypassPermissions", store.InboxOf("s-1")!.PermissionMode);
    }

    [Fact]
    public async Task An_absurd_permission_mode_is_not_stored()
    {
        await HookAsync("SessionStart", "s-1", new string('x', 500));
        Assert.Null(store.InboxOf("s-1")!.PermissionMode);
    }

    [Fact]
    public async Task The_stream_hands_an_ask_to_the_local_table_once_and_a_cancel_closes_it()
    {
        using var fx = new RemoteFixture();
        var relay = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock);
        var ask = new AskMessage(Guid.Parse(Id(1)), "which branch?", "build-box", "git:host/shop", fx.Clock.GetUtcNow().AddMinutes(2));

        Assert.True(await relay.OnStreamAsync(AgentStreamEvents.Ask, JsonSerializer.SerializeToElement(ask, ApiClient.Json), CancellationToken.None));
        Assert.True(await relay.OnStreamAsync(AgentStreamEvents.Ask, JsonSerializer.SerializeToElement(ask with { Question = "replayed" }, ApiClient.Json), CancellationToken.None));
        var saved = fx.Store.AskIn(Id(1))!;
        Assert.Equal(("which branch?", "build-box", "git:host/shop", LocalStore.AskQueued), (saved.Question, saved.FromLabel, saved.Project, saved.State));

        Assert.True(await relay.OnStreamAsync(AgentStreamEvents.AskCancel, JsonSerializer.SerializeToElement(new AskCancelMessage(Guid.Parse(Id(1))), ApiClient.Json), CancellationToken.None));
        Assert.Equal(LocalStore.AskClosed, fx.Store.AskIn(Id(1))!.State);
    }

    [Fact]
    public void A_line_in_the_transcript_that_holds_the_asks_wrapper_confirms_the_delivery_and_a_bare_id_does_not()
    {
        store.SaveAskIn(new AskMessage(Guid.Parse(Id(1)), "q", "box", null, Start.AddMinutes(2)), Start);
        Assert.True(store.AskDeliveredTo(Id(1), "s-1", Start));
        var transcript = Path.Combine(home.Dir, "t.jsonl");
        Directory.CreateDirectory(home.Dir);
        // Claude Code writes the wrapper's angle brackets as they are; the default encoder would write them as \u003C and hide the marker.
        var raw = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(transcript, JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = $"see {Id(1)} somewhere" } }, raw) + "\n");
        store.TrackTranscript(new TranscriptCursor("s-1", HarnessKinds.ClaudeCode, transcript, 0, null, null, null), Start);
        var tailer = new TranscriptTailer(home.Config, store, clock);
        tailer.RunOnce();
        Assert.Equal(LocalStore.AskDelivered, store.AskIn(Id(1))!.State); // the bare id is not the wrapper

        File.AppendAllText(transcript, JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = $"<<<claude-monitor-message id={Id(1)} origin=\"machine\" from=\"box\"\nq\nclaude-monitor-message>>>" } }, raw) + "\n");
        tailer.RunOnce();
        Assert.Equal(LocalStore.AskConfirmed, store.AskIn(Id(1))!.State);
    }

    [Fact]
    public void Text_a_claude_wrote_is_masked_before_it_leaves_the_machine_and_keeps_its_lines()
    {
        var token = "ghp_" + new string('b', 36);
        var masked = TextMask.Mask($"the key is {token}\nsecond line");
        Assert.DoesNotContain(token, masked, StringComparison.Ordinal);
        Assert.Contains("[masked:github_token]", masked, StringComparison.Ordinal);
        Assert.Contains("\nsecond line", masked, StringComparison.Ordinal);
        Assert.Equal("", TextMask.Mask(null));
    }

    [Fact]
    public void The_ask_settings_come_from_the_environment_within_their_ranges()
    {
        AgentConfig With(string name, string? value) => AgentConfig.FromEnvironment(k => k == name ? value : null);
        Assert.True(With("CM_ASKS", null).AsksEnabled);
        Assert.False(With("CM_ASKS", "off").AsksEnabled);
        Assert.True(With("CM_ASKS", "on").AsksEnabled);
        Assert.Equal(TimeSpan.FromSeconds(30), With("CM_ASK_IDLE_FOR", null).AskIdleFor);
        Assert.Equal(TimeSpan.FromSeconds(5), With("CM_ASK_IDLE_FOR", "1").AskIdleFor);
        Assert.Equal(TimeSpan.FromSeconds(600), With("CM_ASK_IDLE_FOR", "99999").AskIdleFor);
        Assert.Equal(TimeSpan.FromMinutes(2), With("CM_ASK_CONFIRM_WAIT", null).AskConfirmWait);
        Assert.Equal(TimeSpan.FromSeconds(30), With("CM_ASK_CONFIRM_WAIT", "5").AskConfirmWait);
    }
}

/// <summary>The capability "ask" (ADR-0008 §3e): the inbox push is on, asks are not turned off here, and Claude Code is on the machine.</summary>
public sealed class AskCapabilityTests : IDisposable
{
    private readonly SessionsDir files = new();
    private RemoteFixture? fx;
    private ClaudePresence? claude = new(ClaudeKinds.Native, null, "2.1.300", 0, 0, default);

    public void Dispose()
    {
        fx?.Dispose();
        files.Dispose();
    }

    private sealed class NoMetrics : IMetricsSource
    {
        public MetricSample? Sample(DateTimeOffset now) => null;
    }

    private async Task<IReadOnlyList<string>?> CapabilitiesAsync(Func<AgentConfig, AgentConfig> tweak)
    {
        fx = new RemoteFixture(c => tweak(c with { ClaudeConfigDir = files.ConfigDir }));
        fx.Fake.On("PUT /api/agent/profile", RemoteFixture.NoContent);
        var monitor = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), () => new Exec.ExecPolicy(ExecLevels.Off, [], []), () => (claude, []));
        await monitor.ProfileAsync(CancellationToken.None);
        return JsonSerializer.Deserialize<AgentProfile>(fx.Fake.Seen.Last(s => s.Path == "/api/agent/profile").Body, ApiClient.Json)!.Capabilities;
    }

    [Fact]
    public async Task Push_through_the_inbox_asks_on_and_claude_present_reports_ask()
    {
        Assert.Equal([AgentCapabilities.Ask], await CapabilitiesAsync(c => c with { PushEnabled = true, PushTransport = PushTransports.Inbox }));
    }

    [Fact]
    public async Task No_push_asks_turned_off_or_no_claude_reports_no_ask()
    {
        Assert.Empty((await CapabilitiesAsync(c => c))!);
        fx?.Dispose();
        Assert.Empty((await CapabilitiesAsync(c => c with { PushEnabled = true, PushTransport = PushTransports.Inbox, AsksEnabled = false }))!);
        fx?.Dispose();
        claude = new ClaudePresence(ClaudeKinds.None, null, null, 0, 0, default);
        Assert.Empty((await CapabilitiesAsync(c => c with { PushEnabled = true, PushTransport = PushTransports.Inbox }))!);
    }
}
