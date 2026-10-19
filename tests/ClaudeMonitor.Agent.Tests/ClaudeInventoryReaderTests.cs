using System.Text.Json;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>What the agent reads of Claude Code's state, read-only (ADR-0011 §7a, step E): every part, its limits, and what is never read or sent.</summary>
public sealed class ClaudeInventoryReaderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly SessionsDir files = new();
    private readonly TempHome home = new();
    private readonly string project = Path.Combine(Path.GetTempPath(), "cm-inv-test-" + Guid.NewGuid().ToString("N"), "shop");
    private readonly FakeCli cli = new();
    private readonly AgentConfig config;

    public ClaudeInventoryReaderTests()
    {
        config = home.Config with { ClaudeConfigDir = files.ConfigDir };
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        File.WriteAllText(Path.Combine(project, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(project, ".git", "config"), "[remote \"origin\"]\n\turl = git@host.example:team/shop.git\n");
    }

    public void Dispose()
    {
        files.Dispose();
        home.Dispose();
        Directory.Delete(Path.GetDirectoryName(project)!, recursive: true);
    }

    private sealed class FakeCli : IClaudeCli
    {
        public int Calls { get; private set; }
        public string Output { get; set; } = "[]";
        public int Exit { get; set; }

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(["plugin", "list", "--json"], args);
            return Task.FromResult(new ClaudeRun(Exit, Output, ""));
        }
    }

    private ClaudeInventoryReader Reader(bool installed = true) => new(config, cli,
        () => new ClaudeInstall(installed ? ClaudeKind.Npm : ClaudeKind.NotFound, installed ? "/usr/local/bin/claude" : null, ""), _ => true);

    private Task<ClaudeInventory> ReadAsync(ClaudeInventoryReader reader, DateTimeOffset? at = null) =>
        reader.ReadAsync([files.ConfigDir], [project], at ?? Start, CancellationToken.None);

    private void WriteSession(int pid, object body) => files.Write($"{pid}.json", JsonSerializer.Serialize(body));

    [Fact]
    public async Task Sessions_come_from_the_session_files_with_the_project_key_of_their_folder_and_only_known_statuses_and_kinds()
    {
        WriteSession(10, new { pid = 10, name = "fix the login", cwd = project, kind = "interactive", status = "idle", startedAt = Start.AddMinutes(-5).ToUnixTimeMilliseconds() });
        WriteSession(11, new { pid = 11, name = "bad\u0007name", cwd = "/nowhere/at/all", kind = "Weird Kind!", status = "exploding", startedAt = Start.AddMinutes(-1).ToUnixTimeMilliseconds() });

        var sessions = (await ReadAsync(Reader())).Sessions!;

        Assert.Equal(2, sessions.Count);
        Assert.Equal(("fix the login", "git:host.example/team/shop", "interactive", "idle"), (sessions[0].Name, sessions[0].ProjectKey, sessions[0].Kind, sessions[0].Status));
        Assert.Equal(("badname", null, null, null), (sessions[1].Name, sessions[1].ProjectKey, sessions[1].Kind, sessions[1].Status));
    }

    [Fact]
    public async Task Plugins_come_from_claude_plugin_list_json_without_the_install_path_and_only_with_a_safe_id()
    {
        cli.Output = JsonSerializer.Serialize(new object[]
        {
            new { id = "adyusuf@adyusuf-marketplace", version = "3a60fbd", scope = "user", enabled = true, installPath = "/Users/someone/.claude/plugins/cache/x", installedAt = "2026-10-17" },
            new { id = "off@m", version = "1", scope = "project", enabled = false },
            new { id = "has space@m", version = "1", enabled = true },
            "not an object",
        });

        var plugins = (await ReadAsync(Reader())).Plugins!;

        Assert.Equal(["adyusuf@adyusuf-marketplace", "off@m"], plugins.Select(p => p.Id));
        Assert.Equal((true, "user", "3a60fbd"), (plugins[0].Enabled, plugins[0].Scope, plugins[0].Version));
        Assert.False(plugins[1].Enabled);
        Assert.DoesNotContain("someone", JsonSerializer.Serialize(plugins), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "not json")]
    [InlineData(0, "{\"not\":\"a list\"}")]
    [InlineData(0, "not json")]
    public async Task A_failed_or_unreadable_plugin_list_is_reported_as_unknown_not_as_empty(int exit, string output)
    {
        cli.Exit = exit;
        cli.Output = output;
        Assert.Null((await ReadAsync(Reader())).Plugins);
    }

    [Fact]
    public async Task Without_claude_on_the_machine_plugins_are_unknown_and_the_cli_is_never_run()
    {
        Assert.Null((await ReadAsync(Reader(installed: false))).Plugins);
        Assert.Equal(0, cli.Calls);
    }

    [Fact]
    public async Task The_plugin_list_is_asked_once_per_interval_and_again_after_it()
    {
        var reader = Reader();
        await ReadAsync(reader);
        await ReadAsync(reader, Start.AddMinutes(5));
        Assert.Equal(1, cli.Calls);

        await ReadAsync(reader, Start + ClaudeInventoryReader.PluginsEvery);
        Assert.Equal(2, cli.Calls);
    }

    [Fact]
    public async Task Skills_are_folder_names_with_a_skill_file_the_users_and_those_of_plugins_under_the_config_folder_and_nothing_inside_them_is_read()
    {
        Directory.CreateDirectory(Path.Combine(files.ConfigDir, "skills", "mine"));
        File.WriteAllText(Path.Combine(files.ConfigDir, "skills", "mine", "SKILL.md"), "SECRET-SKILL-TEXT");
        Directory.CreateDirectory(Path.Combine(files.ConfigDir, "skills", "no-skill-file"));
        var inside = Path.Combine(files.ConfigDir, "plugins", "cache", "p", "1");
        Directory.CreateDirectory(Path.Combine(inside, "skills", "review"));
        File.WriteAllText(Path.Combine(inside, "skills", "review", "SKILL.md"), "x");
        var outside = Path.Combine(files.Root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(outside, "skills", "sneaky"));
        File.WriteAllText(Path.Combine(outside, "skills", "sneaky", "SKILL.md"), "x");
        cli.Output = JsonSerializer.Serialize(new object[]
        {
            new { id = "p@m", version = "1", enabled = true, installPath = inside },
            new { id = "q@m", version = "1", enabled = true, installPath = outside },
        });

        var inventory = await ReadAsync(Reader());

        Assert.Equal(["mine", "p:review"], inventory.Skills);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(inventory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_settings_are_read_from_the_three_files_and_only_values_of_a_closed_shape_are_kept()
    {
        var claude = Path.Combine(project, ".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, "mode"), "B\n");
        File.WriteAllText(Path.Combine(claude, "mode-model"), "sonnet\n");
        File.WriteAllText(Path.Combine(claude, "adyusuf.json"), """
            {"merge": {"dev": true, "prod": false}, "push": {"prod": false}, "run_before_push": {"test": {"unit": true}},
             "note": "free text is ignored", "Bad Name": true, "n": 5}
            """);

        var setting = Assert.Single((await ReadAsync(Reader())).Settings!);

        Assert.Equal(("git:host.example/team/shop", "B", "sonnet"), (setting.ProjectKey, setting.Mode, setting.ModeModel));
        Assert.Equal(new Dictionary<string, bool> { ["merge.dev"] = true, ["merge.prod"] = false, ["push.prod"] = false, ["run_before_push.test.unit"] = true }, setting.Gates);
    }

    [Fact]
    public async Task A_mode_that_is_not_one_letter_a_to_e_a_model_with_odd_characters_and_a_huge_file_are_dropped()
    {
        var claude = Path.Combine(project, ".claude");
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(claude, "mode"), "Z");
        File.WriteAllText(Path.Combine(claude, "mode-model"), "Sonnet; rm -rf /");
        File.WriteAllText(Path.Combine(claude, "adyusuf.json"), "{\"push\":{\"dev\":true}," + new string(' ', 20_000) + "}");

        Assert.Empty((await ReadAsync(Reader())).Settings!);
    }

    [Fact]
    public async Task A_folder_with_none_of_the_files_has_no_settings_entry()
    {
        Assert.Empty((await ReadAsync(Reader())).Settings!);
    }
}
