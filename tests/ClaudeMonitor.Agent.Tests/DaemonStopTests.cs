using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>How an update stops a running daemon: a request file the daemon watches for; and what a daemon says about itself afterwards.</summary>
public sealed class DaemonStopTests : IDisposable
{
    private readonly TempHome home = new(c => c with
    {
        FlushEvery = TimeSpan.FromMilliseconds(20),
        HeartbeatEvery = TimeSpan.FromMilliseconds(50),
        StopPollEvery = TimeSpan.FromMilliseconds(20),
    });

    private readonly FakeApi fake = new();
    private readonly CancellationTokenSource cancel = new(TimeSpan.FromSeconds(60)); // a safety net only: every test ends the daemon itself

    public DaemonStopTests()
    {
        fake.On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "")
            .On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid()))
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(50);
                return (HttpStatusCode.OK, "");
            });
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
    }

    public void Dispose()
    {
        cancel.Cancel();
        cancel.Dispose();
        home.Dispose();
    }

    private Task<int> Start() => new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake).RunAsync(cancel.Token);

    private string? Stored(string key)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        return store.Get(key);
    }

    /// <summary>The daemon has set itself up (its stale-request cleanup is done) and has been answered at least once.</summary>
    private async Task Running() => await Until.True(() => Stored(UpdateHealth.VersionKey) is not null && Stored(Relay.LastContactKey) is not null);

    [Fact]
    public async Task A_stop_request_ends_the_daemon_cleanly_removes_the_request_and_frees_the_lock()
    {
        var run = Start();
        await Running();

        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop");

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(cancel.IsCancellationRequested);
        Assert.False(File.Exists(home.Config.StopRequestPath));
        using var free = DaemonHost.TryLock(home.Config.LockPath);
        Assert.NotNull(free);
        Assert.Contains("stop requested", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stop_request_left_behind_by_an_earlier_daemon_is_deleted_and_not_obeyed()
    {
        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stale");
        var run = Start();
        await Running();
        Assert.False(File.Exists(home.Config.StopRequestPath));

        var beats = fake.Count("POST /api/agent/heartbeat");
        await Until.True(() => fake.Count("POST /api/agent/heartbeat") >= beats + 3); // it keeps working
        Assert.False(run.IsCompleted);

        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop"); // a fresh one is obeyed
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task A_stop_request_stamped_after_the_daemon_took_its_lock_is_obeyed_one_stamped_before_is_not()
    {
        // what DaemonControl writes is its own clock's time: a request from before the start belongs to an earlier daemon
        await File.WriteAllTextAsync(home.Config.StopRequestPath, DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"));
        var first = Start();
        await Running();
        Assert.False(first.IsCompleted);
        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop");
        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(15)));

        // the daemon is about to start and the request lands just after it took the lock: it is ours
        await File.WriteAllTextAsync(home.Config.StopRequestPath, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        Assert.Equal(0, await Start().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(File.Exists(home.Config.StopRequestPath));
    }

    [Fact]
    public async Task The_daemon_says_which_version_it_is_and_when_the_api_last_answered_so_an_update_can_judge_it_healthy()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var run = Start();
        await Running();

        using (var store = new LocalStore(home.Config.DatabasePath))
        {
            Assert.Equal(AgentConfig.Version, store.Get(UpdateHealth.VersionKey));
            Assert.True(UpdateHealth.IsHealthy(store, AgentConfig.Version, before));
            Assert.False(UpdateHealth.IsHealthy(store, "99.0.0", before), "another version is not this daemon");
            Assert.False(UpdateHealth.IsHealthy(store, AgentConfig.Version, DateTimeOffset.UtcNow.AddHours(1)), "an answer from before the update does not count");
        }

        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void Health_needs_both_markers()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.False(UpdateHealth.IsHealthy(store, "1.0.0", since));
        store.Set(UpdateHealth.VersionKey, "1.0.0");
        Assert.False(UpdateHealth.IsHealthy(store, "1.0.0", since)); // never answered
        store.Set(Relay.LastContactKey, "not a date");
        Assert.False(UpdateHealth.IsHealthy(store, "1.0.0", since));
        store.Set(Relay.LastContactKey, DateTimeOffset.UtcNow.ToString("O"));
        Assert.True(UpdateHealth.IsHealthy(store, "1.0.0", since));
    }

    [Fact]
    public async Task The_daemon_clears_what_an_update_left_beside_the_binary_but_keeps_the_previous_version()
    {
        var bin = home.Config.BinaryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        await File.WriteAllTextAsync(bin, "BINARY");
        await File.WriteAllTextAsync(BinarySwap.Previous(bin), "PREV");
        await File.WriteAllTextAsync(bin + ".bad", "x");
        await File.WriteAllTextAsync(bin + ".old12345678", "x");

        var run = Start();
        await Running();
        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal("BINARY", await File.ReadAllTextAsync(bin));
        Assert.Equal("PREV", await File.ReadAllTextAsync(BinarySwap.Previous(bin)));
        Assert.False(File.Exists(bin + ".bad"));
        Assert.False(File.Exists(bin + ".old12345678"));
    }

    // ---- the real DaemonControl --------------------------------------------------------------------------------

    private DaemonControl Control(TimeProvider? clock = null) => new(home.Config, new AgentLog(home.Config, TimeProvider.System), clock ?? TimeProvider.System);

    [Fact]
    public async Task Stopping_a_running_daemon_returns_true_and_the_daemon_is_gone()
    {
        var run = Start();
        await Running();
        var control = Control();
        Assert.True(control.IsRunning());

        Assert.True(await control.StopAsync(TimeSpan.FromSeconds(15), CancellationToken.None));

        Assert.False(control.IsRunning());
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(File.Exists(home.Config.StopRequestPath));
    }

    [Fact]
    public async Task Stopping_when_nothing_runs_returns_true_at_once_and_leaves_no_request()
    {
        var control = Control();
        Assert.False(control.IsRunning());
        Assert.True(await control.StopAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.False(File.Exists(home.Config.StopRequestPath));
    }

    [Fact]
    public async Task A_holder_that_never_lets_go_makes_the_stop_fail_and_leaves_no_request_for_the_next_daemon()
    {
        using var stuck = DaemonHost.TryLock(home.Config.LockPath);
        Assert.NotNull(stuck);
        var clock = new VirtualClock(DateTimeOffset.UtcNow);
        var control = Control(clock);
        Assert.True(control.IsRunning());

        Assert.False(await control.StopAsync(TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.False(File.Exists(home.Config.StopRequestPath));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(30), "it waited the whole time before giving up");
    }

    [Fact]
    public void The_real_control_starts_nothing_when_a_daemon_already_holds_the_lock()
    {
        using var held = DaemonHost.TryLock(home.Config.LockPath);
        Assert.NotNull(held);
        Assert.True(Control().Start(Path.Combine(home.Dir, "no-such-binary"))); // already running: nothing to start, nothing launched
    }
}
