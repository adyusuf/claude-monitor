using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Podium.Agent.Capture;
using Podium.Agent.Config;
using Podium.Agent.Push;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The inbox push where it meets the world (ADR-0007): the real socket, the hooks that learn it, the settings and the install.</summary>
public sealed class InboxWireTests : IDisposable
{
    private const string Token = "aaaa-bbbb-test";
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new(c => c with { PushEnabled = true, PushTransport = PushTransports.Inbox, InboxFormat = InboxFormats.Full, MessagingSocket = "/tmp/cc-socks/9.sock", MessagingToken = Token });
    private readonly ManualClock clock = new(Start);
    private readonly LocalStore store;

    public InboxWireTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private Task<string?> Hook(string hook, AgentConfig? config = null, string session = "s-1") =>
        new HookRunner(config ?? home.Config, store, clock).RunAsync(hook, JsonSerializer.Serialize(new { session_id = session, cwd = home.Dir }), CancellationToken.None);

    // ---- the real transport ------------------------------------------------------------------------------------------

    /// <summary>A short path: a Unix socket path is limited to about 100 characters.</summary>
    private static string SocketPath() => Path.Combine("/tmp", "apt-" + Guid.NewGuid().ToString("N")[..10] + ".sock");

    [Fact]
    public async Task The_real_transport_writes_both_lines_to_a_socket_in_order_and_closes()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = SocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            var read = Task.Run(async () =>
            {
                using var peer = await listener.AcceptAsync();
                var all = new List<byte>();
                var buffer = new byte[256];
                int n;
                while ((n = await peer.ReceiveAsync(buffer)) > 0) all.AddRange(buffer.AsSpan(0, n).ToArray());
                return Encoding.UTF8.GetString([.. all]);
            });
            await new SocketInboxTransport().SendAsync(path, ["{\"type\":\"auth\"}", "{\"type\":\"user\",\"x\":\"é\"}"], TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("{\"type\":\"auth\"}\n{\"type\":\"user\",\"x\":\"é\"}\n", await read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_socket_nobody_listens_on_fails_and_a_path_that_is_no_endpoint_is_refused_before_connecting()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAnyAsync<SocketException>(() => new SocketInboxTransport().SendAsync(SocketPath(), ["x"], TimeSpan.FromSeconds(1), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SocketInboxTransport().SendAsync("relative.sock", ["x"], TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(SocketInboxTransport.IsEndpoint(null));
        Assert.False(SocketInboxTransport.IsEndpoint(""));
        Assert.False(SocketInboxTransport.IsEndpoint("/tmp/a\0b.sock"));
        Assert.False(SocketInboxTransport.IsEndpoint(@"\\.\pipe\x")); // a pipe name is not a path on this OS
        Assert.True(SocketInboxTransport.IsEndpoint("/tmp/cc-socks/1.sock"));
    }

    [Fact]
    public async Task The_pump_and_the_real_socket_deliver_a_prompt_end_to_end()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = SocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            store.SaveInbox("s-1", path, Token, Start);
            store.SaveCommand(new LocalCommand("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e60", "s-1", CommandKinds.Prompt, "deploy it?", Start.AddMinutes(30)));
            var read = Task.Run(async () =>
            {
                using var peer = await listener.AcceptAsync();
                var buffer = new byte[8192];
                var n = await peer.ReceiveAsync(buffer);
                await Task.Delay(50);
                return Encoding.UTF8.GetString(buffer, 0, n);
            });
            Assert.Equal(1, await new InboxPump(() => home.Config, store, new SocketInboxTransport(), clock).PumpAsync(CancellationToken.None));
            var lines = (await read.WaitAsync(TimeSpan.FromSeconds(5))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("auth", JsonNode.Parse(lines[0])!["type"]!.GetValue<string>());
            Assert.Contains("deploy it?", JsonNode.Parse(lines[1])!["message"]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the hooks learn where a session listens -----------------------------------------------------------------------

    [Fact]
    public async Task Any_hook_of_a_session_stores_its_socket_and_key_and_the_session_end_forgets_them()
    {
        await Hook("PreToolUse");
        var inbox = store.InboxOf("s-1")!;
        Assert.Equal(("/tmp/cc-socks/9.sock", Token), (inbox.Endpoint, inbox.Token));
        await Hook("SessionEnd");
        Assert.Null(store.InboxOf("s-1"));
    }

    [Fact]
    public async Task Nothing_is_kept_while_push_is_off_or_through_the_channel_or_when_the_hook_has_no_valid_socket()
    {
        await Hook("PreToolUse", home.Config with { PushEnabled = false });
        await Hook("PreToolUse", home.Config with { PushTransport = PushTransports.Channel });
        await Hook("PreToolUse", home.Config with { MessagingSocket = null });
        await Hook("PreToolUse", home.Config with { MessagingSocket = "relative.sock" });
        Assert.Empty(store.Inboxes());
        await Hook("PreToolUse");
        Assert.NotNull(store.InboxOf("s-1"));
        await Hook("PreToolUse", home.Config with { PushEnabled = false }); // switched off: the key is not kept
        Assert.Null(store.InboxOf("s-1"));
    }

    // ---- settings, status and install -----------------------------------------------------------------------------------

    [Fact]
    public void Push_on_still_means_the_channel_the_inbox_is_asked_for_by_name_and_the_environment_wins()
    {
        AgentConfig Env(string? push) => AgentConfig.FromEnvironment(k => k switch { "AP_AGENT_HOME" => home.Dir, "AP_PUSH" => push, _ => null });
        Assert.Equal((true, PushTransports.Channel), (Env("on").PushEnabled, Env("on").PushTransport)); // what it meant before the inbox existed
        Assert.Equal((true, PushTransports.Channel), (Env("channel").PushEnabled, Env("channel").PushTransport));
        Assert.Equal((true, PushTransports.Inbox), (Env("inbox").PushEnabled, Env("inbox").PushTransport));
        Assert.False(Env("off").PushEnabled);
        Assert.False(Env(null).PushEnabled);
        var socket = AgentConfig.FromEnvironment(k => k switch { "CLAUDE_CODE_MESSAGING_SOCKET" => "/tmp/cc-socks/1.sock", "CLAUDE_CODE_MESSAGING_TOKEN" => "t", _ => null });
        Assert.Equal(("/tmp/cc-socks/1.sock", "t"), (socket.MessagingSocket, socket.MessagingToken?.Value));
        Assert.Null(AgentConfig.FromEnvironment(_ => null).MessagingSocket);

        var plain = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home.Dir : null);
        SavedSettings.SavePush(plain, true, PushTransports.Inbox);
        Assert.Equal(PushTransports.Inbox, SavedSettings.Apply(plain).PushTransport);
        SavedSettings.SavePush(plain, true); // no transport named: the saved one stays
        Assert.Equal(PushTransports.Inbox, SavedSettings.Apply(plain).PushTransport);
        SavedSettings.SavePush(plain, true, PushTransports.Channel);
        Assert.Equal(PushTransports.Channel, SavedSettings.Apply(plain).PushTransport);
        Assert.Equal(PushTransports.Channel, PushTransports.Normalize("anything else"));
    }

    [Fact]
    public void An_agent_json_saved_before_the_inbox_existed_keeps_pushing_through_the_channel()
    {
        var plain = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home.Dir : null);
        File.WriteAllText(plain.IdentityPath, "{\"machineKey\":\"" + new string('a', 24) + "\",\"push\":true}"); // the file an older agent wrote
        var applied = SavedSettings.Apply(plain);
        Assert.Equal((true, PushTransports.Channel), (applied.PushEnabled, applied.PushTransport));
        Assert.False(applied.InboxPushEnabled);
        Assert.True(applied.ChannelPushEnabled);
    }

    [Fact]
    public async Task Config_push_saves_the_choice_without_installing_anything()
    {
        var plain = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home.Dir : null);
        async Task<(int, string)> Config(params string[] a)
        {
            var o = new StringWriter();
            return (await Cli.RunAsync(["config", .. a], plain, new StringReader(""), o, new StringWriter(), TimeProvider.System), o.ToString());
        }

        Assert.Contains("on through the inbox", (await Config("push", "inbox")).Item2, StringComparison.Ordinal);
        Assert.True(SavedSettings.Apply(plain).InboxPushEnabled);
        Assert.Contains("on through the channel", (await Config("push", "on")).Item2, StringComparison.Ordinal);
        Assert.True(SavedSettings.Apply(plain).ChannelPushEnabled);
        Assert.Contains("push: off", (await Config("push", "off")).Item2, StringComparison.Ordinal);
        Assert.False(SavedSettings.Apply(plain).PushEnabled);
        Assert.Equal(2, (await Config("push", "maybe")).Item1);
        Assert.False(Directory.Exists(plain.PluginDir)); // nothing was registered with Claude Code
    }

    [Fact]
    public void The_session_key_cannot_reach_a_log_through_the_configuration_and_the_daemon_does_not_inherit_it()
    {
        var text = home.Config.ToString();
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.Contains("[secret]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, $"{home.Config.MessagingToken}", StringComparison.Ordinal);

        var info = new System.Diagnostics.ProcessStartInfo("x");
        info.Environment["CLAUDE_CODE_MESSAGING_TOKEN"] = Token;
        info.Environment["CLAUDE_CODE_MESSAGING_SOCKET"] = "/tmp/cc-socks/9.sock";
        info.Environment["PATH_KEPT"] = "yes";
        Daemon.DaemonHost.Scrub(info);
        Assert.False(info.Environment.ContainsKey("CLAUDE_CODE_MESSAGING_TOKEN"));
        Assert.False(info.Environment.ContainsKey("CLAUDE_CODE_MESSAGING_SOCKET"));
        Assert.Equal("yes", info.Environment["PATH_KEPT"]);
    }

    [Fact]
    public async Task A_socket_that_is_a_symlink_is_not_written_to()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = SocketPath();
        var link = SocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(real));
        listener.Listen(1);
        File.CreateSymbolicLink(link, real);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new SocketInboxTransport().SendAsync(link, ["x"], TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.True(SocketInboxTransport.IsOurs(real));
            Assert.False(SocketInboxTransport.IsOurs(link));
        }
        finally
        {
            File.Delete(link);
            File.Delete(real);
        }
    }

    [Fact]
    public async Task A_plugin_started_agent_takes_the_stop_wait_only_as_far_as_its_plugin_versions_hook_timeout_allows()
    {
        var sixty = TimeSpan.FromSeconds(60);
        var id = "aipod@aipod-releases";
        // An older plugin version's wrapper says nothing about a long Stop timeout: its fixed 15 s would cut a wait off, so the wait is zero.
        Assert.Equal(TimeSpan.Zero, SavedSettings.Apply(home.Config with { PluginId = id, StopWait = sixty }).StopWait);
        // A version with the long timeout says how long a wait fits.
        var longer = home.Config with { PluginId = id, PluginStopWaitMax = TimeSpan.FromSeconds(590) };
        Assert.Equal(sixty, SavedSettings.Apply(longer with { StopWait = sixty }).StopWait);
        Assert.Equal(TimeSpan.FromSeconds(100), SavedSettings.Apply(home.Config with { PluginId = id, PluginStopWaitMax = TimeSpan.FromSeconds(100), StopWait = TimeSpan.FromSeconds(300) }).StopWait);
        // A daemon the plugin did not start is not held to any plugin's timeout.
        Assert.Equal(sixty, SavedSettings.Apply(home.Config with { StopWait = sixty }).StopWait);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_plugins_wrapper_says_how_long_a_stop_wait_fits_from_the_environment()
    {
        Assert.Equal(TimeSpan.Zero, AgentConfig.FromEnvironment(_ => null).PluginStopWaitMax);
        Assert.Equal(TimeSpan.FromSeconds(590), AgentConfig.FromEnvironment(k => k == "AP_PLUGIN_STOP_WAIT_MAX" ? "590" : null).PluginStopWaitMax);
        Assert.Equal(TimeSpan.FromSeconds(590), AgentConfig.FromEnvironment(k => k == "AP_PLUGIN_STOP_WAIT_MAX" ? "9999" : null).PluginStopWaitMax); // never above what a person can set
    }

    [Fact]
    public void The_live_settings_follow_a_later_install_without_a_restart()
    {
        var plain = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home.Dir : null) with { PushSettingsEvery = TimeSpan.FromSeconds(5) };
        var live = new LiveSettings(plain, clock);
        Assert.False(live.Current().PushEnabled);
        SavedSettings.SavePush(plain, true, PushTransports.Inbox);
        Assert.False(live.Current().PushEnabled); // not read again yet
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(live.Current().InboxPushEnabled);
    }

    [Fact]
    public void The_status_says_which_session_inbox_is_known_what_failed_and_never_the_key()
    {
        store.SaveInbox("s-1", "/tmp/cc-socks/9.sock", Token, Start);
        store.Set(LocalStore.InboxErrorKey, "SocketException");
        store.Set(LocalStore.InboxErrorAtKey, Start.AddSeconds(-5).ToString("O"));
        store.SaveCommand(new LocalCommand("w-1", "s-1", CommandKinds.Prompt, "x", Start.AddMinutes(9)));
        var known = string.Join(' ', PushStatus.Describe(home.Config, store, Start, "s-1", daemonRunning: true));
        Assert.Contains("on through the inbox socket", known, StringComparison.Ordinal);
        Assert.Contains("this session's inbox is known", known, StringComparison.Ordinal);
        Assert.Contains("Last inbox failure: SocketException at 11:59:55Z.", known, StringComparison.Ordinal);
        Assert.Contains("1 waiting for a hook", known, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, known, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/cc-socks", known, StringComparison.Ordinal);
        Assert.Contains("not known yet", string.Join(' ', PushStatus.Describe(home.Config, store, Start, "s-2", daemonRunning: true)), StringComparison.Ordinal);
        Assert.Contains("not bound yet", string.Join(' ', PushStatus.Describe(home.Config, store, Start, null, daemonRunning: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_push_inbox_uses_the_inbox_and_adds_no_channel_to_the_plugin()
    {
        var source = Path.Combine(home.Dir, "aipod-src");
        File.WriteAllText(source, "binary");
        var plain = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home.Dir : null);
        (int Code, string Out) Install(params string[] extra)
        {
            var o = new StringWriter();
            return (Cli.Install(["install", .. extra], plain, o, new StringWriter(), source, (_, _) => 0), o.ToString());
        }

        string Manifest() => File.ReadAllText(Path.Combine(plain.PluginDir, "aipod", ".claude-plugin", "plugin.json"));
        var on = Install("--push", "inbox");
        Assert.Contains("through each session's inbox", on.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("--dangerously-load-development-channels", on.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("channels", Manifest(), StringComparison.Ordinal);
        Assert.Equal(0, Install("--push", "channel").Code);
        Assert.Contains("channels", Manifest(), StringComparison.Ordinal);
        Assert.Equal(0, Install("--push", "inbox").Code);
        Assert.DoesNotContain("channels", Manifest(), StringComparison.Ordinal);
        var legacy = Install("--push", "on"); // "on" has always meant the channel
        Assert.Contains("through a channel", legacy.Out, StringComparison.Ordinal);
        Assert.Contains("channels", Manifest(), StringComparison.Ordinal);
    }
}
