using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Starting and stopping Claude in the background from the web (ADR-0014), with a fake `claude`: what is run, in which folder, what is refused, and what is remembered.</summary>
public sealed class ClaudeControlTests : IDisposable
{
    private const string Project = "git:host/shop";
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClaude : IClaudeCli
    {
        public List<(string[] Args, string? Folder)> Runs { get; } = [];

        public Func<string[], ClaudeRun> Answer { get; set; } = args => args[0] == "--bg"
            ? new ClaudeRun(0, "backgrounded · 34a6ea9d · aipod-shop\n  claude agents   list sessions\n", "")
            : new ClaudeRun(0, $"stopped {args[1]}\n", "");

        public Exception? Throws { get; set; }

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add(([.. args], workingDirectory));
            if (Throws is not null) throw Throws;
            return Task.FromResult(Answer([.. args]));
        }
    }

    private readonly TempHome home = new();
    private readonly ManualClock clock = new(Start);
    private readonly FakeClaude claude = new();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-shop-").FullName;
    private ClaudeInstall install = new(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install");

    public ClaudeControlTests()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.SeeFolder(Project, folder, Start);
    }

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private ClaudeControl Control(AgentConfig? config = null) => new(config ?? home.Config, claude, clock, () => install);

    // ---- start ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "manual")]
    [InlineData("plan", "plan")]
    [InlineData("acceptEdits", "acceptEdits")]
    public async Task A_start_runs_claude_bg_in_the_project_folder_with_a_fixed_shape_and_remembers_the_session(string? asked, string used)
    {
        var outcome = await Control().StartAsync(Project, asked, null, CancellationToken.None);

        Assert.Equal(new ClaudeControlOutcome(true, $"started session 34a6ea9d in the background ({used} mode); it appears in the session list", "34a6ea9d"), outcome);
        var run = Assert.Single(claude.Runs);
        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", used], run.Args);
        Assert.Equal(folder, run.Folder);
        var worker = Assert.Single(Control().Running());
        Assert.Equal(("34a6ea9d", Project, folder, used, Start), (worker.Id, worker.ProjectKey, worker.Folder, worker.Mode, worker.StartedAt));
    }

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("auto")]
    [InlineData("dontAsk")]
    [InlineData("manual; rm -rf /")]
    [InlineData("")]
    public async Task A_permission_mode_outside_the_closed_list_is_refused_and_claude_is_never_run(string mode)
    {
        var outcome = await Control().StartAsync(Project, mode, null, CancellationToken.None);
        Assert.False(outcome.Succeeded);
        Assert.Empty(claude.Runs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\nkey")]
    public async Task A_start_without_a_usable_project_key_is_refused(string? key)
    {
        Assert.False((await Control().StartAsync(key, null, null, CancellationToken.None)).Succeeded);
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task A_project_this_machine_does_not_know_and_a_folder_that_is_gone_are_refused_without_running_claude()
    {
        var unknown = await Control().StartAsync("git:host/other", null, null, CancellationToken.None);
        Assert.Contains("knows no such project", unknown.Text, StringComparison.Ordinal);

        Directory.Delete(folder, recursive: true);
        try
        {
            var gone = await Control().StartAsync(Project, null, null, CancellationToken.None);
            Assert.Contains("folder is gone", gone.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.CreateDirectory(folder); // for the cleanup
        }

        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task A_listed_folder_counts_as_a_project_too()
    {
        using var listed = new TempHome();
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        Assert.True(ProjectFolderList.Add(listed.Config, folder));
        var key = Capture.ProjectInfo.Resolve(folder)!.Key;

        var outcome = await new ClaudeControl(listed.Config, claude, clock, () => install).StartAsync(key, null, null, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(folder, Assert.Single(claude.Runs).Folder);
    }

    [Fact]
    public async Task A_service_agent_and_a_machine_that_claims_to_be_production_never_start_a_session()
    {
        var service = await Control(home.Config with { ServiceMode = true }).StartAsync(Project, null, null, CancellationToken.None);
        Assert.Contains("runs as a service", service.Text, StringComparison.Ordinal);

        (Identity.Load(home.Config) with { ClaimedEnvironment = MachineEnvironments.Production }).Save(home.Config);
        var production = await Control().StartAsync(Project, null, null, CancellationToken.None);
        Assert.Contains("production or database", production.Text, StringComparison.Ordinal);
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task Without_a_claude_on_the_machine_nothing_is_run_and_the_text_says_why()
    {
        install = new ClaudeInstall(ClaudeKind.NotFound, null, "no `claude` on the PATH");
        var outcome = await Control().StartAsync(Project, null, null, CancellationToken.None);
        Assert.Contains("no `claude` was found", outcome.Text, StringComparison.Ordinal);
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task No_more_than_the_limit_of_sessions_started_from_the_web_run_at_once_and_a_stopped_one_frees_its_place()
    {
        var small = home.Config with { ClaudeWorkersMax = 1 };
        Assert.True((await Control(small).StartAsync(Project, null, null, CancellationToken.None)).Succeeded);

        var second = await Control(small).StartAsync(Project, null, null, CancellationToken.None);
        Assert.False(second.Succeeded);
        Assert.Contains("1 sessions started from the web are already running", second.Text, StringComparison.Ordinal);

        Assert.True((await Control(small).StopAsync("34a6ea9d", CancellationToken.None)).Succeeded);
        claude.Answer = _ => new ClaudeRun(0, "backgrounded · 0123abcd · aipod-shop\n", "");
        Assert.True((await Control(small).StartAsync(Project, null, null, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task A_folder_claude_code_does_not_trust_is_reported_as_that_with_the_next_step()
    {
        claude.Answer = _ => new ClaudeRun(1, "", "Workspace not trusted. Run `claude` in /x once and accept the trust prompt, then retry.\n");

        var outcome = await Control().StartAsync(Project, null, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("does not trust that folder yet", outcome.Text, StringComparison.Ordinal);
        Assert.Empty(Control().Running());
    }

    [Fact]
    public async Task Another_failure_reports_the_exit_code_and_the_first_line_cleaned_and_cut_and_a_claude_that_will_not_start_is_reported()
    {
        claude.Answer = _ => new ClaudeRun(2, "", "\u001b[31mboom\u001b[0m " + new string('x', 400) + "\nsecond line");
        var failed = await Control().StartAsync(Project, null, null, CancellationToken.None);
        Assert.StartsWith("claude --bg failed (exit 2): ", failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', failed.Text);
        Assert.DoesNotContain("second line", failed.Text, StringComparison.Ordinal);
        Assert.True(failed.Text.Length < 260);

        claude.Answer = _ => new ClaudeRun(0, "something else entirely\n", "");
        Assert.Contains("did not say which session", (await Control().StartAsync(Project, null, null, CancellationToken.None)).Text, StringComparison.Ordinal);

        claude.Throws = new Win32Exception("no such file");
        Assert.Equal("claude could not be started", (await Control().StartAsync(Project, null, null, CancellationToken.None)).Text);
        Assert.Empty(Control().Running());
    }

    [Theory]
    [InlineData("git:host/Shop Online!", "aipod-Online")]
    [InlineData("git:host/shop", "aipod-shop")]
    [InlineData("dir:abcdef", "aipod-abcdef")]
    [InlineData("!!!", "aipod-project")]
    [InlineData("///", "aipod-project")]
    public void The_session_name_is_a_short_label_of_safe_characters(string key, string name) =>
        Assert.Equal(name, ClaudeControl.NameFor(key.Replace("Shop Online!", "Shop/Online")));

    // ---- stop ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_runs_claude_stop_for_a_session_this_agent_started_and_forgets_it_as_running()
    {
        await Control().StartAsync(Project, null, null, CancellationToken.None);
        claude.Runs.Clear();

        var outcome = await Control().StopAsync("34a6ea9d", CancellationToken.None);

        Assert.Equal(new ClaudeControlOutcome(true, "stopped session 34a6ea9d; its conversation is kept"), outcome);
        var run = Assert.Single(claude.Runs);
        Assert.Equal(["stop", "34a6ea9d"], run.Args);
        Assert.Null(run.Folder);
        Assert.Empty(Control().Running());
        Assert.False((await Control().StopAsync("34a6ea9d", CancellationToken.None)).Succeeded); // already stopped
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--all")]
    [InlineData("34a6ea9")]
    [InlineData("34A6EA9D")]
    public async Task A_stop_that_is_not_a_short_session_id_is_refused_and_never_reaches_claude(string? id)
    {
        Assert.False((await Control().StopAsync(id, CancellationToken.None)).Succeeded);
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task A_session_the_agent_did_not_start_is_never_stopped_even_if_it_exists()
    {
        await Control().StartAsync(Project, null, null, CancellationToken.None);
        claude.Runs.Clear();

        var outcome = await Control().StopAsync("deadbeef", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not start that session", outcome.Text, StringComparison.Ordinal);
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task A_stop_that_fails_leaves_the_session_listed_as_running_and_says_what_claude_said()
    {
        await Control().StartAsync(Project, null, null, CancellationToken.None);
        claude.Answer = _ => new ClaudeRun(1, "", "no such session\n");

        var outcome = await Control().StopAsync("34a6ea9d", CancellationToken.None);

        Assert.Equal(new ClaudeControlOutcome(false, "claude stop failed (exit 1): no such session"), outcome);
        Assert.Single(Control().Running());
        claude.Throws = new IOException();
        Assert.Equal("claude could not be started", (await Control().StopAsync("34a6ea9d", CancellationToken.None)).Text);
        install = new ClaudeInstall(ClaudeKind.NotFound, null, "none");
        Assert.Contains("no `claude` was found", (await Control().StopAsync("34a6ea9d", CancellationToken.None)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_list_of_sessions_counts_as_none_of_ours_so_nothing_is_stopped_on_a_guess()
    {
        await File.WriteAllTextAsync(home.Config.ClaudeWorkersPath, "not json at all");

        Assert.Empty(Control().Running());
        Assert.False((await Control().StopAsync("34a6ea9d", CancellationToken.None)).Succeeded);
        Assert.True((await Control().StartAsync(Project, null, null, CancellationToken.None)).Succeeded); // and a new start rewrites it
        Assert.Single(Control().Running());
    }

    // ---- through the action relay -------------------------------------------------------------------------------

    [Fact]
    public async Task The_relay_hands_a_claude_start_to_the_control_and_reports_the_session_id_and_a_stop_without_one()
    {
        using var fx = new RemoteFixture();
        var reports = new ConcurrentQueue<AgentActionReport>();
        var start = new AgentActionMessage(Guid.NewGuid(), AgentActionKinds.ClaudeStart, DateTimeOffset.UtcNow.AddMinutes(10), Project, "plan");
        var stop = new AgentActionMessage(Guid.NewGuid(), AgentActionKinds.ClaudeStop, DateTimeOffset.UtcNow.AddMinutes(10), SessionRef: "34a6ea9d");
        foreach (var action in new[] { start, stop })
        {
            fx.Fake.On($"POST /api/agent/actions/{action.Id}/status", body =>
            {
                reports.Enqueue(JsonSerializer.Deserialize<AgentActionReport>(body, ApiClient.Json)!);
                return (HttpStatusCode.NoContent, "");
            });
        }

        using var work = new TempHome();
        using (var store = new LocalStore(work.Config.DatabasePath)) store.SeeFolder(Project, folder, Start);
        var relay = new ActionRelay(fx.Api, new AgentLog(fx.Home.Config, TimeProvider.System), () => Task.FromResult("s"),
            _ => Task.FromResult(new UpdateCheck(UpdateCodes.UpToDate, "ok")), _ => true, false, new ClaudeControl(work.Config, claude, clock, () => install));

        await relay.PerformAsync(start, CancellationToken.None);
        await relay.PerformAsync(stop, CancellationToken.None);

        var all = reports.ToArray();
        Assert.Equal([AgentActionStatuses.Delivered, AgentActionStatuses.Succeeded, AgentActionStatuses.Delivered, AgentActionStatuses.Succeeded], all.Select(r => r.Status));
        Assert.Equal("34a6ea9d", all[1].SessionRef);
        Assert.Null(all[3].SessionRef);
        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", "plan"], claude.Runs[0].Args);
    }

    [Fact]
    public async Task A_relay_without_a_claude_control_refuses_the_two_kinds_in_words()
    {
        using var fx = new RemoteFixture();
        var reports = new ConcurrentQueue<AgentActionReport>();
        var action = new AgentActionMessage(Guid.NewGuid(), AgentActionKinds.ClaudeStart, DateTimeOffset.UtcNow.AddMinutes(10), Project);
        fx.Fake.On($"POST /api/agent/actions/{action.Id}/status", body =>
        {
            reports.Enqueue(JsonSerializer.Deserialize<AgentActionReport>(body, ApiClient.Json)!);
            return (HttpStatusCode.NoContent, "");
        });
        var relay = new ActionRelay(fx.Api, new AgentLog(fx.Home.Config, TimeProvider.System), () => Task.FromResult("s"),
            _ => Task.FromResult(new UpdateCheck(UpdateCodes.UpToDate, "ok")), _ => true);

        await relay.PerformAsync(action, CancellationToken.None);

        Assert.Equal(AgentActionStatuses.Failed, reports.Last().Status);
        Assert.Contains("cannot start or stop Claude", reports.Last().Result, StringComparison.Ordinal);
    }
}
