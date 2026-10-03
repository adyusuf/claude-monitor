using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    private async Task<(int Code, string Out)> Cli_(params string[] args)
    {
        var output = new StringWriter();
        var code = await Cli.RunAsync(args, home.Config, new StringReader(""), output, new StringWriter(), clock);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Usage_version_and_status()
    {
        Assert.Contains("cm-agent login", (await Cli_()).Out, StringComparison.Ordinal);
        Assert.Equal(2, (await Cli_("frobnicate")).Code);
        Assert.Equal(AgentConfig.Version + Environment.NewLine, (await Cli_("version")).Out);
        var (code, status) = await Cli_("status");
        Assert.Equal(1, code);
        Assert.Contains("not connected", status, StringComparison.Ordinal);
        Assert.Contains("daemon: not running", status, StringComparison.Ordinal);
        Assert.Equal("--x", Cli.Option(["a", "--server", "--x"], "--server"));
        Assert.Null(Cli.Option(["--server"], "--server"));
    }

    [Fact]
    public async Task A_hook_always_exits_zero_and_prints_its_answer()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var log = new AgentLog(home.Config, clock);
        Assert.Equal(0, await Cli.HookAsync("Stop", home.Config, new StringReader("{not json"), stdout, stderr, clock, log));
        Assert.Contains("cm-agent hook Stop", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("hook Stop failed", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);

        using (var store = new LocalStore(home.Config.DatabasePath))
        {
            store.SaveCommand(new LocalCommand("c", "s1", CommandKinds.Stop, null, clock.GetUtcNow().AddMinutes(1)));
        }

        Assert.Equal(0, await Cli.HookAsync("PreToolUse", home.Config, new StringReader("""{"session_id":"s1"}"""), stdout, stderr, clock, log));
        Assert.Contains("\"continue\":false", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_runs_the_device_flow_and_saves_identity_and_tokens()
    {
        var fake = new FakeApi();
        fake.On("POST /api/device/code", HttpStatusCode.OK, new DeviceCodeResponse("dev", "BCDF-GHJK", "https://monitor.invalid/device", 0, 600));
        var polls = 0;
        var agentId = Guid.NewGuid();
        fake.On("POST /api/device/token", _ => ++polls switch
        {
            1 => (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            2 => (HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new TokenResponse("a", "r", clock.GetUtcNow(), clock.GetUtcNow(), agentId, Guid.NewGuid()), Net.ApiClient.Json)),
        });
        var output = new StringWriter();
        string? opened = null;
        var login = new Login(home.Config, output, new FastClock(clock), url => (opened = url) is not null, "macos");
        Assert.Equal(0, await login.RunAsync("https://monitor.invalid/", fake, CancellationToken.None));
        Assert.Contains("BCDF-GHJK", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("https://monitor.invalid/device?code=BCDF-GHJK", opened);
        var identity = Identity.Load(home.Config);
        Assert.Equal(("https://monitor.invalid", agentId), (identity.Server, identity.AgentId));
        Assert.Equal("r", Credentials.For(home.Config).Read(Credentials.Refresh));
        var request = JsonSerializer.Deserialize<DeviceCodeRequest>(fake.Seen.First().Body, Net.ApiClient.Json)!;
        Assert.Equal(identity.MachineKey, request.MachineKey);

        Assert.Equal(0, await login.LogoutAsync());
        Assert.Null(Credentials.For(home.Config).Read(Credentials.Access));
        Assert.False(Identity.Load(home.Config).Connected);
    }

    [Theory]
    [InlineData("access_denied", "denied")]
    [InlineData("expired_token", "expired")]
    public async Task Login_stops_on_a_denied_or_expired_code(string error, string said)
    {
        var fake = new FakeApi()
            .On("POST /api/device/code", HttpStatusCode.OK, new DeviceCodeResponse("dev", "BCDF-GHJK", "https://m.invalid/device", 0, 600))
            .On("POST /api/device/token", HttpStatusCode.BadRequest, $$"""{"error":"{{error}}"}""");
        var output = new StringWriter();
        Assert.Equal(1, await new Login(home.Config, output, new FastClock(clock), _ => true, "windows").RunAsync("https://m.invalid", fake, CancellationToken.None));
        Assert.Contains(said, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_needs_an_https_server_or_localhost()
    {
        var output = new StringWriter();
        Assert.Equal(2, await new Login(home.Config, output, clock, _ => true, "macos").RunAsync("http://monitor.invalid", null, CancellationToken.None));
        Assert.Equal(2, await new Login(home.Config, output, clock, _ => true, "macos").RunAsync(null, null, CancellationToken.None));
        Assert.Equal(2, await new Login(home.Config, output, clock, _ => true, "unsupported").RunAsync("https://m.invalid", null, CancellationToken.None));
        Assert.Contains("macOS and Windows", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_writes_a_local_marketplace_whose_hooks_run_the_installed_binary()
    {
        var calls = new List<string>();
        var output = new StringWriter();
        var installer = new PluginInstaller(home.Config, output, (tool, args) =>
        {
            calls.Add(tool + " " + string.Join(' ', args));
            return 0;
        });
        var source = Path.Combine(home.Dir, "downloaded-cm-agent");
        File.WriteAllText(source, "binary");
        Assert.Equal(0, installer.Install(source));
        Assert.Equal("binary", File.ReadAllText(installer.BinaryPath));
        Assert.Equal(["claude plugin marketplace add " + home.Config.PluginDir, "claude plugin install monitor-agent@monitor-agent-local --scope user"], calls);

        var hooks = JsonNode.Parse(File.ReadAllText(Path.Combine(home.Config.PluginDir, "monitor-agent", "hooks", "hooks.json")))!["hooks"]!;
        var permission = hooks["PermissionRequest"]![0]!["hooks"]![0]!;
        Assert.Equal(installer.BinaryPath, permission["command"]!.GetValue<string>());
        Assert.Equal(17, permission["timeout"]!.GetValue<int>()); // 2 s wait in the test home + 15
        Assert.Null(permission["async"]);
        Assert.True(hooks["PostToolUse"]![0]!["hooks"]![0]!["async"]!.GetValue<bool>());
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(home.Config.PluginDir, "monitor-agent", ".mcp.json")))!;
        Assert.Equal("mcp", mcp["mcpServers"]!["claude-monitor"]!["args"]![0]!.GetValue<string>());
        Assert.Equal("monitor-agent-local", JsonNode.Parse(File.ReadAllText(Path.Combine(home.Config.PluginDir, ".claude-plugin", "marketplace.json")))!["name"]!.GetValue<string>());

        Assert.Equal(0, installer.Uninstall());
        Assert.Contains("claude plugin uninstall monitor-agent@monitor-agent-local", calls);
    }

    [Fact]
    public void Install_without_claude_prints_the_commands_to_run()
    {
        var output = new StringWriter();
        var installer = new PluginInstaller(home.Config, output, (_, _) => 127);
        var source = Path.Combine(home.Dir, "cm");
        File.WriteAllText(source, "b");
        Assert.Equal(1, installer.Install(source));
        Assert.Contains("claude plugin marketplace add", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mcp_tools_report_status_and_post_notes()
    {
        var tools = new MonitorTools(home.Config, clock);
        Assert.StartsWith("Not connected", tools.Status(), StringComparison.Ordinal);
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);
        Assert.StartsWith("Connected to https://m.invalid", tools.Status(), StringComparison.Ordinal);
        Assert.Equal("Posted.", tools.Note("s1", new string('n', 3000)));
        Assert.StartsWith("Nothing posted", tools.Note("", "x"), StringComparison.Ordinal);
        using var store = new LocalStore(home.Config.DatabasePath);
        var e = Assert.Single(store.NextBatch(10, int.MaxValue)!.Value.Rows).Event;
        Assert.Equal(EventKinds.Note, e.Kind);
        Assert.Equal(MonitorTools.NoteMax, e.Payload.GetProperty("text").GetString()!.Length);
    }
}

/// <summary>A clock whose delays end at once: the login's polling interval is not what the test is about.</summary>
public sealed class FastClock(TimeProvider inner) : TimeProvider
{
    private long ticks;

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow().AddSeconds(Interlocked.Increment(ref ticks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
}
