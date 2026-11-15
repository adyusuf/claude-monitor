using Podium.Agent.ClaudeUpdate;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;

namespace Podium.Agent.Tests;

/// <summary>Several starts at once (the actions loop and the auto-start loop share one <see cref="ClaudeControl"/>): the cap holds and no worker record is lost.</summary>
public sealed class ClaudeControlParallelTests : IDisposable
{
    private const string Project = "git:host/shop";
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A `claude --bg` that takes a moment (so starts overlap) and names a different session every time.</summary>
    private sealed class SlowClaude : IClaudeCli
    {
        private int started;

        public int Runs => Volatile.Read(ref started);

        public async Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            await Task.Delay(40, ct);
            var n = Interlocked.Increment(ref started);
            return new ClaudeRun(0, $"backgrounded · {n:x8} · aipod-shop\n", "");
        }
    }

    private readonly TempHome home = new();
    private readonly ManualClock clock = new(Start);
    private readonly SlowClaude claude = new();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-shop-").FullName;

    public ClaudeControlParallelTests()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.SeeFolder(Project, folder, Start);
    }

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private ClaudeControl Control(AgentConfig config) => new(config, claude, clock, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install"));

    [Fact]
    public async Task Starts_racing_for_the_last_slot_start_only_one_session()
    {
        var control = Control(home.Config with { ClaudeWorkersMax = 1 });

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => control.StartAsync(Project, null, CancellationToken.None)));

        Assert.Equal(1, outcomes.Count(o => o.Succeeded));
        Assert.Equal(1, claude.Runs);
        Assert.Single(control.Running());
    }

    [Fact]
    public async Task Parallel_starts_under_the_cap_all_leave_their_worker_record()
    {
        var control = Control(home.Config with { ClaudeWorkersMax = 50 });

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => control.StartAsync(Project, null, CancellationToken.None)));

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Text));
        Assert.Equal(12, control.Running().Select(w => w.Id).Distinct().Count());
    }
}
