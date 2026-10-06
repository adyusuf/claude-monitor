using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>What a real daemon writes about itself for an update to read (its process id, why its heartbeats fail) and what it leaves alone while an update runs.</summary>
public sealed class DaemonSelfReportTests : IDisposable
{
    private readonly TempHome home = new(c => c with
    {
        FlushEvery = TimeSpan.FromMilliseconds(20),
        HeartbeatEvery = TimeSpan.FromMilliseconds(30),
        StopPollEvery = TimeSpan.FromMilliseconds(20),
    });

    private readonly FakeApi fake = new();
    private readonly CancellationTokenSource cancel = new(TimeSpan.FromSeconds(60)); // a safety net only: every test ends the daemon itself

    public DaemonSelfReportTests()
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

    /// <summary>The daemon has set itself up: stale-request cleanup, pid file and binary clean-up are all done (the version marker is written before them, the first answer after).</summary>
    private async Task SetUp() => await Until.True(() => Stored(UpdateHealth.VersionKey) is not null && Stored(Relay.LastContactKey) is not null);

    private async Task StopAsync(Task<int> run)
    {
        await File.WriteAllTextAsync(home.Config.StopRequestPath, "stop");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    private string Binary()
    {
        var bin = home.Config.BinaryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.WriteAllText(bin, "BINARY");
        return bin;
    }

    // ---- daemon.pid -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_daemon_writes_its_own_process_id_to_daemon_pid()
    {
        var run = Start(); // in this process
        await SetUp();

        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), (await File.ReadAllTextAsync(home.Config.PidPath)).Trim());

        await StopAsync(run);
    }

    // ---- leftovers beside the binary ----------------------------------------------------------------------------

    [Fact]
    public async Task While_an_update_holds_its_lock_the_daemon_leaves_the_staged_file_and_the_other_leftovers_alone()
    {
        var bin = Binary();
        File.WriteAllText(BinarySwap.Staged(bin), "STAGED");
        File.WriteAllText(bin + ".bad", "x");
        using var updating = DaemonHost.TryLock(home.Config.UpdateLockPath);
        Assert.NotNull(updating);

        var run = Start();
        await SetUp();
        await StopAsync(run);

        Assert.Equal("STAGED", File.ReadAllText(BinarySwap.Staged(bin)));
        Assert.True(File.Exists(bin + ".bad"));
        Assert.Equal("BINARY", File.ReadAllText(bin));
    }

    [Fact]
    public async Task With_no_update_running_the_daemon_deletes_a_leftover_staged_file()
    {
        var bin = Binary();
        File.WriteAllText(BinarySwap.Staged(bin), "STAGED");
        File.WriteAllText(BinarySwap.Previous(bin), "PREV");

        var run = Start();
        await SetUp();
        await StopAsync(run);

        Assert.False(File.Exists(BinarySwap.Staged(bin)));
        Assert.Equal("PREV", File.ReadAllText(BinarySwap.Previous(bin))); // the rollback copy is never a leftover
        Assert.Equal("BINARY", File.ReadAllText(bin));
    }

    // ---- why the heartbeat fails ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_heartbeat_the_api_refuses_is_recorded_by_exception_type_and_cleared_once_it_is_answered()
    {
        fake.On("POST /api/agent/heartbeat", HttpStatusCode.InternalServerError, "{}");
        var run = Start();
        await Until.True(() => Stored(Relay.HeartbeatErrorKey) == nameof(ApiException));
        Assert.Null(Stored(Relay.LastContactKey)); // an answer was never received

        fake.On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "");
        await Until.True(() => Stored(Relay.HeartbeatErrorKey) == "" && Stored(Relay.LastContactKey) is not null);

        await StopAsync(run);
    }

    [Fact]
    public async Task A_heartbeat_that_cannot_reach_the_server_is_recorded_as_a_network_failure()
    {
        fake.On("POST /api/agent/heartbeat", _ => throw new HttpRequestException("no route to host"));
        var run = Start();
        await Until.True(() => Stored(Relay.HeartbeatErrorKey) == nameof(HttpRequestException));
        Assert.Null(Stored(Relay.LastContactKey));
        Assert.Contains(Stored(Relay.HeartbeatErrorKey)!, UpdateHealth.NetworkErrors); // the very word an update treats as "the network"

        // end it on a working link: a request that fails at the very moment of the stop is a different (shutdown) path
        fake.On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "");
        await Until.True(() => Stored(Relay.HeartbeatErrorKey) == "");
        await StopAsync(run);
    }
}
