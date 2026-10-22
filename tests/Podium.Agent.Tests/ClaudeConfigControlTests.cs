using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.ClaudeUpdate;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The remote writes (ADR-0011 §7a): plugin commands with a fixed shape that run only for an id `claude plugin list` shows, and the adyusuf files of a project folder.</summary>
public sealed class ClaudeConfigControlTests : IDisposable
{
    private const string Project = "git:host/shop";
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-shop-").FullName;
    private readonly FakeCli cli = new();

    public ClaudeConfigControlTests()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.SeeFolder(Project, folder, Start);
    }

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private sealed class FakeCli : IClaudeCli
    {
        public List<string[]> Runs { get; } = [];
        public string Installed { get; set; } = "[{\"id\":\"a@m\",\"enabled\":true},{\"id\":\"b@m\",\"enabled\":false}]";
        public string Available { get; set; } = "{\"installed\":[],\"available\":[{\"pluginId\":\"new@m\"},{\"pluginId\":\"a@m\"}]}";
        public int Exit { get; set; }

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add([.. args]);
            if (args is ["plugin", "list", "--json"]) return Task.FromResult(new ClaudeRun(0, Installed, ""));
            if (args is ["plugin", "list", "--available", "--json"]) return Task.FromResult(new ClaudeRun(0, Available, ""));
            return Task.FromResult(new ClaudeRun(Exit, Exit == 0 ? "ok\n" : "", Exit == 0 ? "" : "boom: it failed\nmore"));
        }
    }

    private ClaudeConfigControl Control(AgentConfig? config = null) => new(config ?? home.Config, cli, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", ""));

    private string[][] Writes() => [.. cli.Runs.Where(r => r.Length > 1 && r[1] is "enable" or "disable" or "update" or "install")];

    [Theory]
    [InlineData(AgentActionKinds.PluginEnable, "b@m", "enable")]
    [InlineData(AgentActionKinds.PluginDisable, "a@m", "disable")]
    [InlineData(AgentActionKinds.PluginUpdate, "a@m", "update")]
    [InlineData(AgentActionKinds.PluginInstall, "new@m", "install")]
    public async Task A_plugin_action_runs_claude_plugin_with_a_fixed_shape_for_a_known_id(string kind, string id, string verb)
    {
        var outcome = await Control().PluginAsync(kind, id, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Text);
        Assert.Equal(["plugin", verb, id], Assert.Single(Writes()));
    }

    [Theory]
    [InlineData(AgentActionKinds.PluginEnable, "unknown@m", "is not installed here")]
    [InlineData(AgentActionKinds.PluginDisable, "unknown@m", "is not installed here")]
    [InlineData(AgentActionKinds.PluginUpdate, "unknown@m", "is not installed here")]
    [InlineData(AgentActionKinds.PluginInstall, "a@m", "already installed")]
    [InlineData(AgentActionKinds.PluginInstall, "ghost@m", "not offered")]
    public async Task An_id_the_machine_does_not_show_is_never_run(string kind, string id, string said)
    {
        var outcome = await Control().PluginAsync(kind, id, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains(said, outcome.Text, StringComparison.Ordinal);
        Assert.Empty(Writes());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a@")]
    [InlineData("@m")]
    [InlineData("a b@m")]
    [InlineData("a@m; rm -rf /")]
    [InlineData("a@m@n")]
    [InlineData("--accept-command@m")]
    public async Task An_id_of_the_wrong_shape_is_refused_before_anything_is_run(string id)
    {
        var outcome = await Control().PluginAsync(AgentActionKinds.PluginEnable, id, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(cli.Runs);
    }

    [Fact]
    public async Task An_unknown_kind_runs_nothing()
    {
        Assert.False((await Control().PluginAsync("plugin_uninstall", "a@m", CancellationToken.None)).Succeeded);
        Assert.Empty(cli.Runs);
    }

    [Fact]
    public async Task A_failed_command_reports_its_first_line_only()
    {
        cli.Exit = 3;
        var outcome = await Control().PluginAsync(AgentActionKinds.PluginEnable, "b@m", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("exit 3", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("boom: it failed", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("more", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_agent_and_a_production_claim_change_nothing()
    {
        var service = await Control(home.Config with { ServiceMode = true }).PluginAsync(AgentActionKinds.PluginEnable, "b@m", CancellationToken.None);
        Assert.Contains("runs as a service", service.Text, StringComparison.Ordinal);
        Assert.Contains("runs as a service", Control(home.Config with { ServiceMode = true }).Setting(Project, "mode", "B").Text, StringComparison.Ordinal);

        (Identity.Load(home.Config) with { ClaimedEnvironment = MachineEnvironments.Production }).Save(home.Config);
        Assert.Contains("production or database", (await Control().PluginAsync(AgentActionKinds.PluginEnable, "b@m", CancellationToken.None)).Text, StringComparison.Ordinal);
        Assert.Contains("production or database", Control().Setting(Project, "mode", "B").Text, StringComparison.Ordinal);
        Assert.Empty(cli.Runs);
        Assert.False(File.Exists(Path.Combine(folder, ".claude", "mode")));
    }

    [Fact]
    public void The_working_mode_and_the_model_are_written_to_their_files()
    {
        Assert.True(Control().Setting(Project, "mode", "C").Succeeded);
        Assert.True(Control().Setting(Project, "mode_model", "haiku").Succeeded);

        Assert.Equal("C\n", File.ReadAllText(Path.Combine(folder, ".claude", "mode")));
        Assert.Equal("haiku\n", File.ReadAllText(Path.Combine(folder, ".claude", "mode-model")));
    }

    [Fact]
    public void A_gate_is_set_inside_adyusuf_json_and_everything_else_in_the_file_stays()
    {
        var path = Path.Combine(folder, ".claude", "adyusuf.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"push\":{\"dev\":true},\"note\":\"keep me\",\"run_before_push\":{\"dev\":{\"unit\":false}}}");

        Assert.True(Control().Setting(Project, "push.prod", "false").Succeeded);
        Assert.True(Control().Setting(Project, "run_before_push.test.unit", "true").Succeeded);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("push").GetProperty("dev").GetBoolean());
        Assert.False(root.GetProperty("push").GetProperty("prod").GetBoolean());
        Assert.True(root.GetProperty("run_before_push").GetProperty("test").GetProperty("unit").GetBoolean());
        Assert.False(root.GetProperty("run_before_push").GetProperty("dev").GetProperty("unit").GetBoolean());
        Assert.Equal("keep me", root.GetProperty("note").GetString());
    }

    [Fact]
    public void A_gate_creates_the_file_when_there_is_none()
    {
        Assert.True(Control().Setting(Project, "merge.dev", "true").Succeeded);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, ".claude", "adyusuf.json")));
        Assert.True(doc.RootElement.GetProperty("merge").GetProperty("dev").GetBoolean());
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    [InlineData("{\"push\":5}")]
    public void A_file_that_is_not_the_expected_shape_is_left_exactly_as_it_is(string content)
    {
        var path = Path.Combine(folder, ".claude", "adyusuf.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var outcome = Control().Setting(Project, "push.prod", "false");

        Assert.False(outcome.Succeeded);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("mode", "Z")]
    [InlineData("mode", "AB")]
    [InlineData("mode", "")]
    [InlineData("mode_model", "Sonnet; rm -rf /")]
    [InlineData("mode_model", "opus")]
    [InlineData("push.prod", "maybe")]
    [InlineData("push.main", "true")]
    [InlineData("../../etc/passwd", "true")]
    [InlineData("merge", "true")]
    public void A_name_or_value_outside_the_closed_lists_writes_nothing(string name, string value)
    {
        var outcome = Control().Setting(Project, name, value);

        Assert.False(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(folder, ".claude")));
    }

    [Fact]
    public void An_unknown_project_or_a_folder_that_is_gone_writes_nothing()
    {
        Assert.Contains("no such project", Control().Setting("git:host/other", "mode", "B").Text, StringComparison.Ordinal);
        using var gone = new TempHome();
        using (var store = new LocalStore(gone.Config.DatabasePath)) store.SeeFolder(Project, Path.Combine(folder, "missing"), Start);
        Assert.Contains("gone", Control(gone.Config).Setting(Project, "mode", "B").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_claude_folder_that_is_a_link_is_not_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Directory.CreateTempSubdirectory("aipod-elsewhere-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, ".claude"), target);

            var outcome = Control().Setting(Project, "mode", "B");

            Assert.False(outcome.Succeeded);
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task The_relay_routes_the_five_kinds_to_the_executor_and_says_so_when_there_is_none()
    {
        using var fx = new RemoteFixture();
        var reports = new ConcurrentQueue<AgentActionReport>();
        var enable = new AgentActionMessage(Guid.NewGuid(), AgentActionKinds.PluginEnable, DateTimeOffset.UtcNow.AddMinutes(10), Target: "b@m");
        var setting = new AgentActionMessage(Guid.NewGuid(), AgentActionKinds.ProjectSetting, DateTimeOffset.UtcNow.AddMinutes(10), Project, Target: "mode", Value: "D");
        foreach (var action in new[] { enable, setting })
        {
            fx.Fake.On($"POST /api/agent/actions/{action.Id}/status", body =>
            {
                reports.Enqueue(JsonSerializer.Deserialize<AgentActionReport>(body, ApiClient.Json)!);
                return (HttpStatusCode.NoContent, "");
            });
        }

        ActionRelay Relay(ClaudeConfigControl? control) => new(fx.Api, new AgentLog(fx.Home.Config, TimeProvider.System), () => Task.FromResult("s"),
            _ => Task.FromResult(new UpdateCheck(UpdateCodes.UpToDate, "ok")), _ => true, false, null, control);

        await Relay(null).PerformAsync(enable, CancellationToken.None);
        Assert.Contains("cannot change Claude's setup", reports.Last().Result, StringComparison.Ordinal);

        var relay = Relay(Control());
        await relay.PerformAsync(enable, CancellationToken.None);
        await relay.PerformAsync(setting, CancellationToken.None);

        Assert.Equal(["plugin", "enable", "b@m"], Assert.Single(Writes()));
        Assert.Equal("D\n", File.ReadAllText(Path.Combine(folder, ".claude", "mode")));
        Assert.Equal(AgentActionStatuses.Succeeded, reports.Last().Status);
    }
}
