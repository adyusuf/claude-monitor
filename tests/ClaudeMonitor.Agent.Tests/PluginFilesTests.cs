using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Install;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The files the repository publishes as the Claude Code plugin (ADR-0007) and `cm-agent connect`.</summary>
public sealed class PluginFilesTests : IDisposable
{
    private readonly TempHome home = new(c => c with { PollEvery = TimeSpan.FromMilliseconds(10), ConnectWait = TimeSpan.FromMilliseconds(400) });

    public void Dispose() => home.Dispose();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, MarketplacePlugin.MarketplaceFile))) return dir.FullName;
        }

        throw new InvalidOperationException("the repository root was not found");
    }

    // ---- the rendered files are the committed files -----------------------------------------------------------------

    [Fact]
    public void The_committed_marketplace_and_plugin_files_are_what_the_agent_renders()
    {
        var root = RepositoryRoot();
        foreach (var (path, content) in MarketplacePlugin.Render(AgentConfig.Version))
        {
            Assert.True(File.Exists(Path.Combine(root, path)), $"{path} is not committed: run cm-agent plugin-files .");
            Assert.True(content == File.ReadAllText(Path.Combine(root, path)), $"{path} is out of date: run cm-agent plugin-files . and commit it");
        }
    }

    [Fact]
    public void Every_hook_the_local_install_registers_is_in_the_plugin_with_the_same_timing_and_goes_through_the_wrapper()
    {
        var hooks = JsonNode.Parse(MarketplacePlugin.Render("1.0.0")[$"{MarketplacePlugin.PluginFolder}/hooks/hooks.json"])!["hooks"]!.AsObject();
        Assert.Equal(PluginInstaller.Hooks.Select(h => h.Event).Order(), hooks.Select(p => p.Key).Order());
        foreach (var (name, isAsync, timeout) in PluginInstaller.Hooks)
        {
            var handlers = hooks[name]![0]!["hooks"]!.AsArray();
            var hook = handlers.Last()!;
            Assert.Equal($"sh \"${{CLAUDE_PLUGIN_ROOT}}/scripts/run.sh\" hook {name}", hook["command"]!.GetValue<string>());
            Assert.Equal(MarketplacePlugin.Timeout(name, timeout), hook["timeout"]!.GetValue<int>());
            Assert.Equal(isAsync, hook["async"]?.GetValue<bool>() ?? false);
        }

        var start = hooks["SessionStart"]![0]!["hooks"]!.AsArray();
        Assert.Equal(2, start.Count);
        Assert.Contains("ensure.sh", start[0]!["command"]!.GetValue<string>(), StringComparison.Ordinal); // the bootstrap comes first
        Assert.Equal((135, 15), (MarketplacePlugin.Timeout("PermissionRequest", 0), MarketplacePlugin.Timeout("Stop", 0)));
    }

    [Fact]
    public void The_plugin_asks_for_the_server_and_hands_it_to_the_mcp_server_and_names_no_host()
    {
        var files = MarketplacePlugin.Render("1.0.0");
        var manifest = JsonNode.Parse(files[$"{MarketplacePlugin.PluginFolder}/.claude-plugin/plugin.json"])!;
        Assert.Equal(("monitor-agent", "1.0.0"), (manifest["name"]!.GetValue<string>(), manifest["version"]!.GetValue<string>()));
        Assert.True(manifest["userConfig"]!["server"]!["required"]!.GetValue<bool>());
        var mcp = JsonNode.Parse(files[$"{MarketplacePlugin.PluginFolder}/.mcp.json"])!["mcpServers"]![PluginInstaller.McpServerName]!;
        Assert.Equal("${user_config.server}", mcp["env"]!["CM_SERVER"]!.GetValue<string>());
        Assert.Equal(["${CLAUDE_PLUGIN_ROOT}/scripts/run.sh", "mcp"], mcp["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        var marketplace = JsonNode.Parse(files[MarketplacePlugin.MarketplaceFile])!;
        Assert.Equal(AgentConfig.ReleasesMarketplaceName, marketplace["name"]!.GetValue<string>());
        Assert.Equal("./plugin/monitor-agent", marketplace["plugins"]![0]!["source"]!.GetValue<string>());
        Assert.All(files.Values, text => Assert.DoesNotMatch(@"https?://[a-z0-9.-]+\.[a-z]{2,}", text)); // host names live in deployment configuration
        Assert.Throws<ArgumentException>(() => MarketplacePlugin.Render(""));
    }

    [Fact]
    public async Task Writing_the_files_creates_the_folders_and_the_plugin_files_verb_does_it_from_the_command_line()
    {
        var target = Path.Combine(home.Dir, "out");
        Assert.Equal(4, MarketplacePlugin.WriteTo(target, "2.0.0"));
        Assert.Contains("\"version\": \"2.0.0\"", File.ReadAllText(Path.Combine(target, "plugin", "monitor-agent", ".claude-plugin", "plugin.json")), StringComparison.Ordinal);
        var stdout = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["plugin-files", Path.Combine(home.Dir, "out2")], home.Config, new StringReader(""), stdout, new StringWriter(), TimeProvider.System));
        Assert.Contains("4 files written", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plugin_environment_is_read_from_the_variables_the_wrapper_and_claude_code_set()
    {
        var c = AgentConfig.FromEnvironment(k => k switch
        {
            "CM_BINARY" => "/data/bin/cm-agent",
            "CM_PLUGIN_ID" => "monitor-agent@monitor-agent-releases",
            "CLAUDE_PLUGIN_DATA" => "/data",
            "CM_AGENT_HOME" => home.Dir,
            _ => null,
        });
        Assert.Equal(("/data/bin/cm-agent", "monitor-agent@monitor-agent-releases", "/data"), (c.BinaryPath, c.PluginId, c.PluginDataDir));
        var plain = AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? home.Dir : null);
        Assert.StartsWith(home.Dir, plain.BinaryPath, StringComparison.Ordinal);
        Assert.Equal((null, null), (plain.PluginId, plain.PluginDataDir));
    }

    // ---- cm-agent connect --------------------------------------------------------------------------------------------

    private sealed class FakeStarter(AgentConfig config, string? writes) : IDetachedStarter
    {
        public List<string[]> Started { get; } = [];
        public bool Works { get; set; } = true;

        public bool Start(string exe, IReadOnlyList<string> args)
        {
            Started.Add([exe, .. args]);
            if (Works && writes is not null) File.WriteAllText(config.ConnectLogPath, writes);
            return Works;
        }
    }

    private Task<int> Connect(FakeStarter starter, StringWriter output, string? server = "https://monitor.invalid") =>
        new ConnectCommand(home.Config, output, TimeProvider.System, starter).RunAsync(server, "/bin/cm-agent", CancellationToken.None);

    [Fact]
    public async Task Connect_starts_the_login_detached_and_prints_the_code_when_it_appears()
    {
        var starter = new FakeStarter(home.Config, "Open https://monitor.invalid/device and enter the code ABCD-1234\n");
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        var started = Assert.Single(starter.Started);
        Assert.Equal(["/bin/cm-agent", "login", "--server", "https://monitor.invalid", "--output", home.Config.ConnectLogPath, "--detach"], started);
        Assert.Contains("enter the code ABCD-1234", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Approve it on that page", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_shown_a_moment_ago_is_shown_again_and_no_second_login_is_started()
    {
        File.WriteAllText(home.Config.ConnectLogPath, "Open https://monitor.invalid/device and enter the code WXYZ-0001\n");
        var starter = new FakeStarter(home.Config, null);
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        Assert.Empty(starter.Started);
        Assert.Contains("WXYZ-0001", output.ToString(), StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(home.Config.ConnectLogPath, DateTime.UtcNow - TimeSpan.FromHours(1)); // an old code has expired
        Assert.Equal(1, await Connect(starter, new StringWriter()));
        Assert.Single(starter.Started);
    }

    [Fact]
    public async Task Connect_says_what_to_do_when_the_login_cannot_start_or_shows_nothing_and_refuses_a_bad_server()
    {
        var failing = new FakeStarter(home.Config, null) { Works = false };
        var output = new StringWriter();
        Assert.Equal(1, await Connect(failing, output));
        Assert.Contains("Could not start the login", output.ToString(), StringComparison.Ordinal);

        var silent = new StringWriter();
        Assert.Equal(1, await Connect(new FakeStarter(home.Config, null), silent));
        Assert.Contains("did not show a code yet", silent.ToString(), StringComparison.Ordinal);

        foreach (var bad in new[] { "http://monitor.invalid", "monitor.invalid", null })
        {
            var refused = new StringWriter();
            var starter = new FakeStarter(home.Config, null);
            Assert.Equal(2, await Connect(starter, refused, bad));
            Assert.Empty(starter.Started);
            Assert.Contains("Give the server", refused.ToString(), StringComparison.Ordinal);
        }

        var loopback = new StringWriter();
        Assert.Equal(1, await Connect(new FakeStarter(home.Config, null), loopback, "http://localhost:5080")); // loopback http is accepted: it starts and then finds no code
        Assert.DoesNotContain("Give the server", loopback.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_when_already_connected_changes_nothing()
    {
        new Identity("0123456789abcdef0123", "https://monitor.invalid", Guid.NewGuid(), Guid.NewGuid()).Save(home.Config);
        var starter = new FakeStarter(home.Config, null);
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        Assert.Empty(starter.Started);
        Assert.Contains("Already connected to https://monitor.invalid", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_can_write_to_a_file_so_a_detached_login_outlives_the_terminal_and_restart_needs_a_connection()
    {
        var file = Path.Combine(home.Dir, "login.out");
        var stdout = new StringWriter();
        var code = await Cli.RunAsync(["login", "--server", "http://127.0.0.1:1", "--output", file], home.Config with { LoginRequestTimeout = TimeSpan.FromSeconds(2) },
            new StringReader(""), stdout, new StringWriter(), TimeProvider.System);
        Assert.Equal(1, code);
        Assert.Contains("did not answer", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal("", stdout.ToString()); // nothing on the terminal that is gone

        var restart = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["restart"], home.Config, new StringReader(""), restart, new StringWriter(), TimeProvider.System));
        Assert.Contains("Not connected", restart.ToString(), StringComparison.Ordinal);
    }
}
