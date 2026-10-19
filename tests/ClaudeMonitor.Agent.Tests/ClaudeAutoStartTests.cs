using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Automatic mode on_message (ADR-0014 P3): when a worker is started for a held ask, and every limit on it.</summary>
public sealed class ClaudeAutoStartTests : IDisposable
{
    private const string Project = "git:host/shop";
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClaude : IClaudeCli
    {
        public List<string[]> Runs { get; } = [];
        public bool Fail { get; set; }

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add([.. args]);
            return Task.FromResult(Fail ? new ClaudeRun(1, "", "boom") : new ClaudeRun(0, $"backgrounded · {Runs.Count:x8} · aipod-shop\n", ""));
        }
    }

    private readonly TempHome home = new();
    private readonly ManualClock clock = new(Start);
    private readonly FakeClaude claude = new();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-shop-").FullName;
    private readonly LocalStore store;
    private readonly ClaudeAutoStart auto;
    private readonly ClaudeControl control;

    public ClaudeAutoStartTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        store.SeeFolder(Project, folder, Start);
        store.Set(MachineMonitor.ClaudeAutoKey, ClaudeAutoModes.OnMessage);
        control = new ClaudeControl(home.Config, claude, clock, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install"));
        auto = new ClaudeAutoStart(home.Config, control, store, clock);
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private static AskIn Ask(string id = "a1", string? project = Project) => new(id, "other", "q", project, Start.AddMinutes(30), "queued", null, null);

    /// <summary>The ask is seen now and again after the grace: the second sighting is the one that may start a worker.</summary>
    private async Task PassAsync(AskIn ask, bool afterGrace = true)
    {
        await auto.OnUnservedAsync(ask, CancellationToken.None);
        if (afterGrace) clock.Advance(home.Config.ClaudeAutoGrace);
        await auto.OnUnservedAsync(ask, CancellationToken.None);
    }

    [Fact]
    public async Task A_held_ask_that_names_a_known_project_gets_one_manual_worker_after_the_grace_and_nothing_of_the_ask_reaches_claude()
    {
        await PassAsync(Ask());

        var run = Assert.Single(claude.Runs);
        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", "manual"], run);
        var worker = Assert.Single(control.Running());
        Assert.Equal((Project, ClaudeAutoStart.Reason), (worker.ProjectKey, worker.Reason));
    }

    [Fact]
    public async Task Nothing_starts_before_the_grace_has_passed()
    {
        await PassAsync(Ask(), afterGrace: false);
        clock.Advance(home.Config.ClaudeAutoGrace - TimeSpan.FromSeconds(1));
        await auto.OnUnservedAsync(Ask(), CancellationToken.None);
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task With_the_mode_off_or_unknown_nothing_starts()
    {
        store.Set(MachineMonitor.ClaudeAutoKey, ClaudeAutoModes.Off);
        await PassAsync(Ask());
        store.Set(MachineMonitor.ClaudeAutoKey, "always");
        await PassAsync(Ask("a2"));
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task An_ask_that_names_no_project_or_one_this_machine_does_not_know_starts_nothing()
    {
        await PassAsync(Ask("a1", project: null));
        await PassAsync(Ask("a2", project: "git:host/unknown"));
        Assert.Empty(claude.Runs);
    }

    [Fact]
    public async Task There_is_never_a_second_worker_for_the_same_project()
    {
        await PassAsync(Ask("a1"));
        await PassAsync(Ask("a2"));

        Assert.Single(claude.Runs);
    }

    [Fact]
    public async Task A_failed_start_blocks_the_next_one_until_the_backoff_has_passed()
    {
        claude.Fail = true;
        await PassAsync(Ask("a1"));
        await PassAsync(Ask("a2"));
        Assert.Single(claude.Runs); // the second ask waited out its grace but the backoff still holds

        claude.Fail = false;
        clock.Advance(home.Config.ClaudeAutoBackoff);
        await auto.OnUnservedAsync(Ask("a2"), CancellationToken.None);

        Assert.Equal(2, claude.Runs.Count);
        Assert.Single(control.Running());
    }

    [Fact]
    public async Task No_more_than_the_budget_of_starts_an_hour_is_made()
    {
        var config = home.Config with { ClaudeAutoPerHour = 1, ClaudeWorkersMax = 5 };
        var limited = new ClaudeAutoStart(config, new ClaudeControl(config, claude, clock, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install")), store, clock);
        await limited.OnUnservedAsync(Ask("a1"), CancellationToken.None);
        clock.Advance(config.ClaudeAutoGrace);
        await limited.OnUnservedAsync(Ask("a1"), CancellationToken.None);
        Assert.Single(claude.Runs);

        // the worker is stopped by the person; the budget, not the worker, is what holds the next start back
        await control.StopAsync(Assert.Single(control.Running()).Id, CancellationToken.None);
        await limited.OnUnservedAsync(Ask("a3"), CancellationToken.None);
        clock.Advance(config.ClaudeAutoGrace);
        await limited.OnUnservedAsync(Ask("a3"), CancellationToken.None);
        Assert.Equal(2, claude.Runs.Count); // only the stop

        clock.Advance(TimeSpan.FromHours(1));
        await limited.OnUnservedAsync(Ask("a3"), CancellationToken.None);
        Assert.Equal(3, claude.Runs.Count);
    }
}
