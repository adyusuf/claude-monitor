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

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add([.. args]);
            return Task.FromResult(Fail ? new ClaudeRun(1, "", "boom") : new ClaudeRun(0, $"backgrounded · {++next:x8} · aipod-shop\n", ""));
        }
    }

    private readonly TempHome home = new(c => c with { ClaudeAutoWait = TimeSpan.FromSeconds(30) });
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
        await restarted.RunOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await restarted.RunOnceAsync(CancellationToken.None)); // an hour later there is budget again
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
}
