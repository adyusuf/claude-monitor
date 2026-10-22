using System.Text.Json.Nodes;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Install;

namespace Podium.Agent.Tests;

/// <summary>The files the repository publishes as the Claude Code plugin (ADR-0007) and `aipod connect`.</summary>
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
            Assert.True(File.Exists(Path.Combine(root, path)), $"{path} is not committed: run aipod plugin-files .");
            Assert.True(content == File.ReadAllText(Path.Combine(root, path)), $"{path} is out of date: run aipod plugin-files . and commit it");
        }
    }

    [Fact]
    public void The_stop_hook_fits_the_longest_stop_wait_and_the_wrapper_says_so_with_the_same_number()
    {
        var hooks = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), MarketplacePlugin.PluginFolder, "hooks", "hooks.json")))!["hooks"]!.AsObject();
        Assert.True(hooks["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>() >= AgentConfig.WaitMaxSeconds + 10, "a stop wait would be cut off");
        var wrapper = File.ReadAllText(Path.Combine(RepositoryRoot(), MarketplacePlugin.PluginFolder, "scripts", "run.sh"));
        Assert.Contains($"export AP_PLUGIN_STOP_WAIT_MAX={AgentConfig.WaitMaxSeconds}\n", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_hook_the_local_install_registers_is_in_the_plugin_with_the_same_timing_and_goes_through_the_wrapper()
    {
        var hooks = JsonNode.Parse(MarketplacePlugin.Render("1.0.0")[$"{MarketplacePlugin.PluginFolder}/hooks/hooks.json"])!["hooks"]!.AsObject();
        Assert.Equal(PluginInstaller.Hooks.Select(h => h.Event).Distinct().Order(), hooks.Select(p => p.Key).Order());
        foreach (var (name, isAsync, timeout, extra) in PluginInstaller.Hooks)
        {
            var handlers = hooks[name]![0]!["hooks"]!.AsArray();
            var command = $"sh \"${{CLAUDE_PLUGIN_ROOT}}/scripts/run.sh\" hook {name}" + (extra is null ? "" : " " + extra);
            var hook = Assert.Single(handlers, h => h!["command"]!.GetValue<string>() == command)!;
            Assert.Equal(MarketplacePlugin.Timeout(name, timeout), hook["timeout"]!.GetValue<int>());
            Assert.Equal(isAsync, hook["async"]?.GetValue<bool>() ?? false);
        }

        var start = hooks["SessionStart"]![0]!["hooks"]!.AsArray();
        Assert.Equal(3, start.Count); // the bootstrap, the recording hook, the machines block (ADR-0008)
        Assert.Contains("ensure.sh", start[0]!["command"]!.GetValue<string>(), StringComparison.Ordinal); // the bootstrap comes first
        Assert.EndsWith("hook SessionStart --context", start[2]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(start[2]!["async"]); // synchronous: the block must be there before the first prompt
        Assert.Equal((135, AgentConfig.WaitMaxSeconds + 10), (MarketplacePlugin.Timeout("PermissionRequest", 0), MarketplacePlugin.Timeout("Stop", 0))); // the Stop hook fits the longest stop wait
    }

    [Fact]
    public void The_plugin_asks_for_the_server_and_hands_it_to_the_mcp_server_and_names_no_host()
    {
        var files = MarketplacePlugin.Render("1.0.0");
        var manifest = JsonNode.Parse(files[$"{MarketplacePlugin.PluginFolder}/.claude-plugin/plugin.json"])!;
        Assert.Equal(("monitor-agent", "1.0.0"), (manifest["name"]!.GetValue<string>(), manifest["version"]!.GetValue<string>()));
        Assert.True(manifest["userConfig"]!["server"]!["required"]!.GetValue<bool>());
        var mcp = JsonNode.Parse(files[$"{MarketplacePlugin.PluginFolder}/.mcp.json"])!["mcpServers"]![PluginInstaller.McpServerName]!;
        Assert.Equal("${user_config.server}", mcp["env"]!["AP_SERVER"]!.GetValue<string>());
        Assert.Equal(["${CLAUDE_PLUGIN_ROOT}/scripts/run.sh", "mcp"], mcp["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        var marketplace = JsonNode.Parse(files[MarketplacePlugin.MarketplaceFile])!;
        Assert.Equal(AgentConfig.ReleasesMarketplaceName, marketplace["name"]!.GetValue<string>());
        Assert.Equal("./plugin/monitor-agent", marketplace["plugins"]![0]!["source"]!.GetValue<string>());
        Assert.Equal("./plugin/aipod", marketplace["plugins"]![1]!["source"]!.GetValue<string>()); // the repository catalog offers the new plugin too
        Assert.All(files.Values, text => Assert.DoesNotMatch(@"https?://[a-z0-9.-]+\.[a-z]{2,}", text)); // host names live in deployment configuration
        Assert.Throws<ArgumentException>(() => MarketplacePlugin.Render(""));
    }

    [Fact]
    public void The_aipod_plugin_is_the_old_one_under_the_new_name_with_the_same_hooks_and_mcp_server_and_its_own_words()
    {
        var files = MarketplacePlugin.Render("1.0.0");
        var aipod = JsonNode.Parse(files[$"{MarketplacePlugin.AipodFolder}/.claude-plugin/plugin.json"])!;
        Assert.Equal(("aipod", "1.0.0"), (aipod["name"]!.GetValue<string>(), aipod["version"]!.GetValue<string>()));
        Assert.False(aipod["userConfig"]!["server"]!["required"]!.GetValue<bool>()); // a connected machine is not asked again
        Assert.DoesNotContain("Podium", aipod.ToJsonString(), StringComparison.Ordinal);
        foreach (var file in new[] { "hooks/hooks.json", ".mcp.json" })
        {
            Assert.Equal(files[$"{MarketplacePlugin.PluginFolder}/{file}"], files[$"{MarketplacePlugin.AipodFolder}/{file}"]);
        }

        var old = JsonNode.Parse(files[$"{MarketplacePlugin.PluginFolder}/.claude-plugin/plugin.json"])!;
        Assert.StartsWith("Replaced by the aipod plugin", old["description"]!.GetValue<string>(), StringComparison.Ordinal); // the bridge says so in the plugin list
    }

    [Fact]
    public async Task Writing_the_files_creates_the_folders_and_the_plugin_files_verb_does_it_from_the_command_line()
    {
        var target = Path.Combine(home.Dir, "out");
        Assert.Equal(7, MarketplacePlugin.WriteTo(target, "2.0.0"));
        foreach (var plugin in new[] { "monitor-agent", "aipod" })
        {
            Assert.Contains("\"version\": \"2.0.0\"", File.ReadAllText(Path.Combine(target, "plugin", plugin, ".claude-plugin", "plugin.json")), StringComparison.Ordinal);
        }
        var stdout = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["plugin-files", Path.Combine(home.Dir, "out2")], home.Config, new StringReader(""), stdout, new StringWriter(), TimeProvider.System));
        Assert.Contains("7 files written", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plugin_environment_is_read_from_the_variables_the_wrapper_and_claude_code_set()
    {
        var c = AgentConfig.FromEnvironment(k => k switch
        {
            "AP_BINARY" => "/data/bin/aipod",
            "AP_PLUGIN_ID" => "monitor-agent@monitor-agent-releases",
            "CLAUDE_PLUGIN_DATA" => "/data",
            "AP_AGENT_HOME" => home.Dir,
            _ => null,
        });
        Assert.Equal(("/data/bin/aipod", "monitor-agent@monitor-agent-releases", "/data"), (c.BinaryPath, c.PluginId, c.PluginDataDir));
        var plain = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? home.Dir : null);
        Assert.StartsWith(home.Dir, plain.BinaryPath, StringComparison.Ordinal);
        Assert.Equal((null, null), (plain.PluginId, plain.PluginDataDir));
    }

    // ---- aipod connect --------------------------------------------------------------------------------------------

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

    /// <summary>What a detached login leaves: its log, and the server `connect` recorded when it started it.</summary>
    private void Logged(string server, string log)
    {
        File.WriteAllText(home.Config.ConnectLogPath, log);
        File.WriteAllText(home.Config.ConnectServerPath, server);
    }

    private Task<int> Connect(FakeStarter starter, StringWriter output, string? server = "https://aipod.invalid") =>
        new ConnectCommand(home.Config, output, TimeProvider.System, starter).RunAsync(server, "/bin/aipod", CancellationToken.None);

    [Fact]
    public async Task Connect_starts_the_login_detached_and_prints_the_code_when_it_appears()
    {
        var starter = new FakeStarter(home.Config, "Open https://aipod.invalid/device and enter the code ABCD-1234\n");
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        var started = Assert.Single(starter.Started);
        Assert.Equal(["/bin/aipod", "login", "--server", "https://aipod.invalid", "--output", home.Config.ConnectLogPath, "--detach"], started);
        Assert.Contains("enter the code ABCD-1234", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Approve it on that page", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_shown_a_moment_ago_is_shown_again_and_no_second_login_is_started()
    {
        Logged("https://aipod.invalid", "Open https://aipod.invalid/device and enter the code WXYZ-0001\n");
        var starter = new FakeStarter(home.Config, null);
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        Assert.Empty(starter.Started);
        Assert.Contains("WXYZ-0001", output.ToString(), StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(home.Config.ConnectLogPath, DateTime.UtcNow - TimeSpan.FromHours(1)); // an old code has expired
        Assert.Equal(1, await Connect(starter, new StringWriter()));
        Assert.Single(starter.Started);
    }

    [Theory]
    [InlineData("Connected. This machine now reports to Podium.")]
    [InlineData("The request was denied on the web.")]
    [InlineData("The code expired. Run aipod login again.")]
    [InlineData("The server did not answer (HttpRequestException). Check the address and the connection, then run aipod login again.")]
    public async Task A_code_whose_login_has_ended_is_not_shown_again_and_a_new_login_starts(string outcome)
    {
        Logged("https://aipod.invalid", "Open https://aipod.invalid/device and enter the code DEAD-0001\n" + outcome + "\n");
        var starter = new FakeStarter(home.Config, "Open https://aipod.invalid/device and enter the code NEWC-0002\n");
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        Assert.Single(starter.Started);
        Assert.Contains("NEWC-0002", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("DEAD-0001", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_still_being_polled_is_shown_again_but_only_for_the_same_server()
    {
        Logged("https://aipod.invalid", "Open https://aipod.invalid/device and enter the code WAIT-0003\nThe server did not answer (X); trying again in 10 s.\n");
        var same = new FakeStarter(home.Config, null);
        var output = new StringWriter();
        Assert.Equal(0, await Connect(same, output));
        Assert.Empty(same.Started);
        Assert.Contains("WAIT-0003", output.ToString(), StringComparison.Ordinal);

        var other = new FakeStarter(home.Config, "Open https://other.invalid/device and enter the code OTHR-0004\n");
        var shown = new StringWriter();
        Assert.Equal(0, await Connect(other, shown, "https://other.invalid")); // another server: its own login
        Assert.Single(other.Started);
        Assert.Contains("OTHR-0004", shown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_on_a_verification_page_of_another_host_than_the_server_is_still_shown_again()
    {
        Logged("https://api.invalid", "Open https://web.invalid/device and enter the code SPLT-0005\n"); // the page is the API's public origin, not the address typed
        var starter = new FakeStarter(home.Config, null);
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output, "https://api.invalid"));
        Assert.Empty(starter.Started);
        Assert.Contains("SPLT-0005", output.ToString(), StringComparison.Ordinal);
        var fresh = new FakeStarter(home.Config, "Open https://web.invalid/device and enter the code SPLT-0006\n");
        Assert.Equal(0, await Connect(fresh, new StringWriter(), "https://other-api.invalid"));
        Assert.Single(fresh.Started);
        Assert.Equal("https://other-api.invalid", File.ReadAllText(home.Config.ConnectServerPath)); // recorded when a login is started
    }

    [Fact]
    public async Task Restart_stops_a_running_daemon_and_starts_one_from_this_binary_and_says_when_it_cannot()
    {
        new Identity("0123456789abcdef0123", "https://aipod.invalid", Guid.NewGuid(), Guid.NewGuid()).Save(home.Config);
        var daemon = new FakeDaemon(home.Config, TimeProvider.System);
        var output = new StringWriter();
        var log = new Daemon.AgentLog(home.Config, TimeProvider.System);
        Assert.Equal(0, await Cli.RestartAsync(home.Config, log, TimeProvider.System, output, daemon));
        Assert.Equal(1, daemon.Starts);
        Assert.Contains("restarted", output.ToString(), StringComparison.Ordinal);

        var stuck = new FakeDaemon(home.Config, TimeProvider.System) { StopResult = false, KillResult = false };
        var refused = new StringWriter();
        Assert.Equal(1, await Cli.RestartAsync(home.Config, log, TimeProvider.System, refused, stuck));
        Assert.Equal(0, stuck.Starts);
        Assert.Contains("did not stop", refused.ToString(), StringComparison.Ordinal);
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

        foreach (var bad in new[] { "http://aipod.invalid", "aipod.invalid", null })
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
        new Identity("0123456789abcdef0123", "https://aipod.invalid", Guid.NewGuid(), Guid.NewGuid()).Save(home.Config);
        var starter = new FakeStarter(home.Config, null);
        var output = new StringWriter();
        Assert.Equal(0, await Connect(starter, output));
        Assert.Empty(starter.Started);
        Assert.Contains("Already connected to https://aipod.invalid", output.ToString(), StringComparison.Ordinal);
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
