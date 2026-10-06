using ClaudeMonitor.Agent.Daemon;

namespace ClaudeMonitor.Agent.Tests;

public sealed class DaemonRoleTests : IDisposable
{
    private readonly TempHome home = new(c => c with { ServiceMode = true });

    public void Dispose() => home.Dispose();

    [Fact]
    public async Task A_service_without_a_server_and_not_connected_exits_with_one_and_says_why()
    {
        if (OperatingSystem.IsWindows()) return; // the Windows service first checks its profile
        var log = new AgentLog(home.Config, TimeProvider.System);

        var code = await DaemonRole.RunUntilAsync(home.Config, TimeProvider.System, log, CancellationToken.None);

        Assert.Equal(DaemonRole.NotConnected, code);
        Assert.Contains("CM_SERVER is not set", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(home.Config.Home, "login-code.txt")));
        Assert.False(File.Exists(home.Config.DatabasePath), "no daemon was started"); // a started host would have opened the database
    }

    [Fact]
    public async Task A_service_whose_login_is_refused_asks_again_every_retry_period_until_it_is_stopped()
    {
        if (OperatingSystem.IsWindows()) return;
        // A closed port on this machine: the connection is refused at once and nothing leaves it.
        using var service = new TempHome(c => c with { ServiceMode = true, ServerOverride = "https://127.0.0.1:1", LoginRequestTimeout = TimeSpan.FromSeconds(5) });
        var clock = new StallingClock(fireFirst: 2);
        var log = new AgentLog(service.Config, clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = DaemonRole.RunUntilAsync(service.Config, clock, log, stop.Token);
        // Two waits fire at once (each moves the clock by its period); the third never does, so the stop arrives while it waits.
        Assert.True(await ExecHarness.UntilAsync(() => clock.Timers == 3), "the service gave up instead of waiting to retry");
        await stop.CancelAsync();
        var code = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DaemonRole.NotConnected, code);
        Assert.Equal(2 * DaemonRole.LoginRetry, clock.Elapsed);
        Assert.Equal([DaemonRole.LoginRetry, DaemonRole.LoginRetry, DaemonRole.LoginRetry], clock.Periods);
        var text = await File.ReadAllTextAsync(service.Config.LogPath);
        var attempts = text.Split("service login did not complete").Length - 1;
        Assert.Equal(3, attempts); // one before each wait, none after the stop
        Assert.True(File.Exists(Path.Combine(service.Config.Home, "login-code.txt")), "the code file is created for an admin to read");
        Assert.False(File.Exists(service.Config.DatabasePath), "the daemon proper never started without a login");
    }

    [Fact]
    public void The_retry_period_is_thirty_seconds() => Assert.Equal(TimeSpan.FromSeconds(30), DaemonRole.LoginRetry);

    /// <summary>The first timers fire at once and move time by their due time; later ones never fire.</summary>
    private sealed class StallingClock : TimeProvider
    {
        private readonly int fireFirst;
        private readonly Lock gate = new();
        private readonly DateTimeOffset origin = DateTimeOffset.UtcNow;
        private readonly List<TimeSpan> periods = [];
        private DateTimeOffset now;

        public StallingClock(int fireFirst)
        {
            this.fireFirst = fireFirst;
            now = origin;
        }

        public int Timers
        {
            get
            {
                lock (gate) return periods.Count;
            }
        }

        public IReadOnlyList<TimeSpan> Periods
        {
            get
            {
                lock (gate) return [.. periods];
            }
        }

        public TimeSpan Elapsed
        {
            get
            {
                lock (gate) return now - origin;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            bool fire;
            lock (gate)
            {
                periods.Add(dueTime);
                fire = periods.Count <= fireFirst;
                if (fire) now += dueTime;
            }

            if (fire) ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
