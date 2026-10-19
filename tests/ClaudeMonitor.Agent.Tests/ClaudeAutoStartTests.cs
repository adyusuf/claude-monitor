using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The automatic mode on_message (ADR-0014 P3): when it starts a session, when it must not, and the three bounds.</summary>
public sealed class ClaudeAutoStartTests : IDisposable
{
    private const string Project = "git:host/shop";
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClaude : IClaudeCli
    {
        private int next;
        public List<string[]> Runs { get; } = [];
        public bool Fail { get; set; }

        /// <summary>What `claude agents --json` answers (null: it fails).</summary>
        public string? Listing { get; set; }

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            if (args[0] == "agents") return Task.FromResult(Listing is null ? new ClaudeRun(1, "", "no") : new ClaudeRun(0, Listing, ""));
            Runs.Add([.. args]);
            return Task.FromResult(Fail ? new ClaudeRun(1, "", "boom") : new ClaudeRun(0, $"backgrounded · {++next:x8} · aipod-shop\n", ""));
        }
    }

    private readonly TempHome home = new(c => c with { ClaudeAutoWait = TimeSpan.FromSeconds(30), ClaudeAutoReconcileEvery = TimeSpan.FromDays(365) });
    private readonly ManualClock clock = new(Start);
    private readonly FakeClaude claude = new();
    private readonly LocalStore store;
    private readonly Guid workspace = Guid.NewGuid();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-auto-").FullName;
    private bool hasIdleSession;
    private bool strict;

    public ClaudeAutoStartTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        new Identity(new string('k', 24), AgentId: Guid.NewGuid(), WorkspaceId: workspace).Save(home.Config);
        WorkspaceSettings.Tag(store, workspace);
        store.Set(ClaudeAutoStart.ModeKey, ClaudeAutoModes.OnMessage);
        store.SeeFolder(Project, folder, Start);
        store.SeeFolder("git:host/blog", folder, Start); // a second project key that resolves to a folder
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private ClaudeControl Control(AgentConfig? config = null) => new(config ?? home.Config, claude, clock, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "npm"));

    private ClaudeAutoStart Auto(AgentConfig? config = null) =>
        new(() => config ?? home.Config, store, Control(config), (_, _) => hasIdleSession, () => strict, new AgentLog(home.Config, clock), clock);

    private void Ask(int n, string? project = Project) =>
        store.SaveAskIn(new AskMessage(Guid.Parse($"0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e{n:00}"), "what branch is the build on?", "build-box", project, Start.AddHours(3)), Start);

    [Fact]
    public async Task An_ask_nobody_could_take_gets_one_session_in_its_project_after_the_wait_with_the_fixed_arguments_and_no_prompt()
    {
        var auto = Auto();
        Ask(1);

        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None)); // seen now: the wait has not passed
        Assert.Empty(claude.Runs);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));

        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", "manual"], Assert.Single(claude.Runs));
        Assert.Single(new ClaudeControl(home.Config, claude, clock).Running());
    }

    [Fact]
    public async Task An_ask_an_idle_session_can_take_starts_nothing_and_the_wait_begins_again_when_it_cannot_any_more()
    {
        var auto = Auto();
        Ask(1);
        hasIdleSession = true;
        await auto.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Empty(claude.Runs);

        hasIdleSession = false; // the session got busy: the wait starts from here, not from the first look
        await auto.RunOnceAsync(CancellationToken.None);
        Assert.Empty(claude.Runs);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_ask_that_names_no_project_starts_nothing()
    {
        var auto = Auto();
        Ask(1, project: null);
        clock.Advance(TimeSpan.FromMinutes(5));
        await auto.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task Off_is_the_default_and_unread_switches_a_service_agent_and_a_strict_claim_never_start_anything()
    {
        Ask(1);
        async Task<int> Pass(ClaudeAutoStart auto)
        {
            await auto.RunOnceAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
            return await auto.RunOnceAsync(CancellationToken.None);
        }

        store.Set(ClaudeAutoStart.ModeKey, ClaudeAutoModes.Off);
        Assert.Equal(0, await Pass(Auto()));
        store.Set(ClaudeAutoStart.ModeKey, ClaudeAutoModes.OnMessage);
        WorkspaceSettings.Tag(store, Guid.NewGuid()); // read for another workspace: unread, so off
        Assert.Equal(0, await Pass(Auto()));
        WorkspaceSettings.Tag(store, workspace);
        Assert.Equal(0, await Pass(Auto(home.Config with { ServiceMode = true })));
        strict = true;
        Assert.Equal(0, await Pass(Auto()));
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task A_project_that_already_has_a_session_of_ours_gets_no_second_one()
    {
        var auto = Auto();
        Ask(1);
        await auto.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None)); // the ask is still waiting: that session takes it once it is idle

        Assert.Single(claude.Runs);
    }

    [Fact]
    public async Task At_most_the_budget_an_hour_is_started_and_the_hour_is_remembered_across_a_restart()
    {
        var config = home.Config with { ClaudeAutoStartsPerHour = 2, ClaudeWorkersMax = 10 };
        Ask(1);
        // the budget is the machine's, not the project's: two projects use it up, a third waits
        store.SeeFolder("git:host/third", folder, Start);
        Ask(2, "git:host/blog");
        Ask(3, "git:host/third");
        var auto = Auto(config);
        await auto.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, claude.Runs.Count);

        var restarted = Auto(config); // a new object, the same database
        await restarted.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, await restarted.RunOnceAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));
        // the third ask has been unserved since long before the hour passed, so the first pass with budget again starts its session (one of the two passes does, not both)
        var started = await restarted.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        started += await restarted.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, started); // an hour later there is budget again
    }

    [Fact]
    public async Task A_refused_start_backs_the_project_off_for_ten_minutes_and_nothing_else_is_tried_in_it_meanwhile()
    {
        claude.Fail = true;
        var auto = Auto();
        Ask(1);
        await auto.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Single(claude.Runs);

        clock.Advance(TimeSpan.FromMinutes(5));
        await auto.RunOnceAsync(CancellationToken.None);
        Assert.Single(claude.Runs); // still backing off

        clock.Advance(TimeSpan.FromMinutes(6));
        claude.Fail = false;
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, claude.Runs.Count);
    }

    [Fact]
    public async Task The_question_of_the_ask_is_never_in_the_arguments_or_the_log()
    {
        var auto = Auto();
        Ask(1);
        await auto.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        await auto.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(claude.Runs.Single(), arg => arg.Contains("what branch", StringComparison.Ordinal));
        Assert.DoesNotContain("what branch", File.Exists(home.Config.LogPath) ? File.ReadAllText(home.Config.LogPath) : "", StringComparison.Ordinal);
    }

    // ---- the mode "always" and the reconcile --------------------------------------------------------------------------

    private void Always(string? project = Project)
    {
        store.Set(ClaudeAutoStart.ModeKey, ClaudeAutoModes.Always);
        store.Set(ClaudeAutoStart.ProjectKey, project ?? "");
    }

    [Fact]
    public async Task The_mode_always_starts_one_session_in_the_named_project_with_no_ask_at_all_labels_why_and_keeps_to_one()
    {
        Always();
        var auto = Auto();

        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None)); // it has one

        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", "manual"], Assert.Single(claude.Runs));
        Assert.Equal("auto:always", Assert.Single(new ClaudeControl(home.Config, claude, clock, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "npm")).Running()).Reason);
    }

    [Fact]
    public async Task The_mode_always_starts_nothing_without_a_project_and_under_the_same_refusals_as_every_start()
    {
        Always(project: null);
        Assert.Equal(0, await Auto().RunOnceAsync(CancellationToken.None));
        Always();
        strict = true;
        Assert.Equal(0, await Auto().RunOnceAsync(CancellationToken.None));
        strict = false;
        Assert.Equal(0, await Auto(home.Config with { ServiceMode = true }).RunOnceAsync(CancellationToken.None));
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task When_the_session_it_kept_has_ended_the_mode_always_starts_another_within_the_budget_and_backs_off_after_a_failure()
    {
        var config = home.Config with { ClaudeAutoReconcileEvery = TimeSpan.FromSeconds(30), ClaudeAutoStartsPerHour = 2 };
        Always();
        var auto = Auto(config);
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
        claude.Listing = "[]"; // Claude Code no longer lists it

        clock.Advance(TimeSpan.FromMinutes(3)); // past the grace and the reconcile interval
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, claude.Runs.Count);

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None)); // the budget of two an hour is used up
        Assert.Equal(2, claude.Runs.Count);
    }

    [Fact]
    public async Task A_failed_always_start_waits_out_the_backoff_like_any_other()
    {
        Always();
        claude.Fail = true;
        var auto = Auto();
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await auto.RunOnceAsync(CancellationToken.None));
        Assert.Single(claude.Runs);

        claude.Fail = false;
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await auto.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_reconcile_stops_counting_a_session_claude_code_no_longer_lists_but_only_after_the_grace_and_never_on_a_doubt()
    {
        var control = Control();
        var outcome = await control.StartAsync(Project, null, CancellationToken.None);
        var id = outcome.SessionRef!;

        claude.Listing = "[]";
        Assert.Equal(0, await control.ReconcileAsync(TimeSpan.FromMinutes(2), CancellationToken.None)); // too early: the session may not be listed yet
        clock.Advance(TimeSpan.FromMinutes(3));
        claude.Listing = null;
        Assert.Equal(0, await control.ReconcileAsync(TimeSpan.FromMinutes(2), CancellationToken.None)); // the command failed: unknown
        claude.Listing = "not json";
        Assert.Equal(0, await control.ReconcileAsync(TimeSpan.FromMinutes(2), CancellationToken.None));
        claude.Listing = "{\"sessionId\":\"x\"}"; // not an array
        Assert.Equal(0, await control.ReconcileAsync(TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Single(control.Running());

        claude.Listing = "[{\"pid\":1,\"sessionId\":\"" + id + "-aaaa-bbbb\",\"name\":\"x\"}]"; // still listed: the short id is the start of the full one
        Assert.Equal(0, await control.ReconcileAsync(TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Single(control.Running());

        claude.Listing = "[{\"pid\":1,\"sessionId\":\"ffffffff-aaaa\"}]";
        Assert.Equal(1, await control.ReconcileAsync(TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Empty(control.Running());
    }
}
