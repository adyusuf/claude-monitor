using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// "cm-agent daemon": the long-running part, one per user per machine (an exclusive lock on daemon.lock). It runs
/// until the process is stopped or the agent is revoked. Started by the first hook or MCP call that finds it absent
/// (ADR-0002: the agent starts with the harness, not with the OS).
/// </summary>
public sealed partial class DaemonHost(AgentConfig config, TimeProvider clock, AgentLog log, HttpMessageHandler? handler = null)
{
    public async Task<int> RunAsync(CancellationToken stop)
    {
        config.EnsureHome();
        using var lockFile = TryLock(config.LockPath);
        if (lockFile is null) return 0; // another daemon already runs
        var started = clock.GetUtcNow();
        if (!OperatingSystem.IsWindows()) _ = SetSid(); // leave the hook's process group: its end must not end us

        var identity = Identity.Load(config);
        if (!identity.Connected)
        {
            log.Write("not connected: run `cm-agent login --server <url>`");
            return 1;
        }

        // One connection per loop: a SQLite connection is not safe to share between threads, and the loops run at once.
        // They meet in the database file itself (WAL, busy timeout).
        using var flushStore = new LocalStore(config.DatabasePath);
        using var beatStore = new LocalStore(config.DatabasePath);
        using var settingsStore = new LocalStore(config.DatabasePath);
        using var streamStore = new LocalStore(config.DatabasePath);
        using var http = ApiClient.CreateHttp(identity.Server!, handler);
        using var api = new ApiClient(http, Credentials.For(config), config.ApiCallTimeout) { StreamIdleTimeout = config.StreamIdleTimeout };
        var relay = new Relay(config, flushStore, api, clock);
        var beat = new Relay(config, beatStore, api, clock);
        var settings = new Relay(config, settingsStore, api, clock);
        Relay stream;
        var tailer = new TranscriptTailer(config, flushStore, clock);
        using var updateStore = new LocalStore(config.DatabasePath);
        using var remoteStore = new LocalStore(config.DatabasePath);
        using var execStore = new LocalStore(config.DatabasePath);
        using var metricsStore = new LocalStore(config.DatabasePath);
        var remote = new RemoteRelay(config, remoteStore, api, clock);
        var monitor = new MachineMonitor(config, metricsStore, api, clock, MetricsSource.For(AgentConfig.Os));
        var executor = new RunExecutor(config, clock, log, run => ExecGuard.Check(run, monitor.Policy(), AgentConfig.Os, config.Home),
            ExecPolicyLoader.RunningAsRoot);
        using var runs = new RunRelay(config, execStore, api, clock, executor, log);
        runs.Recover();
        stream = new Relay(config, streamStore, api, clock) { Runs = runs };
        using var revoked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        DropStaleStopRequest(started);
        updateStore.Set(UpdateHealth.VersionKey, AgentConfig.Version);
        File.WriteAllText(config.PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using (var updating = TryLock(config.UpdateLockPath))
        {
            if (updating is not null) BinarySwap.CleanLeftovers(config.BinaryPath); // an update in progress owns its staged file
        }

        var updates = new UpdateLoop(config, updateStore, new Updater(config, api, http, new SystemProcessRunner(), new DaemonControl(config, log, clock), log, clock),
            () => DaemonControl.SpawnAutoUpdate(config, log), clock);
        log.Write($"daemon started, version {AgentConfig.Version}");
        await Task.WhenAll(
            StopRequestAsync(revoked),
            Loop("update", config.UpdatePollEvery, updates.RunAsync, api, revoked),
            Loop("flush", config.FlushEvery, async ct =>
            {
                tailer.RunOnce();
                await relay.PermissionsAsync(ct);
                await relay.UploadAsync(ct);
                await relay.ReportCommandsAsync(ct);
            }, api, revoked, error => relay.UploadOutcome(error)),
            Loop("heartbeat", config.HeartbeatEvery, beat.HeartbeatAsync, api, revoked, beat.HeartbeatFailed),
            Loop("settings", config.SettingsEvery, async ct =>
            {
                await settings.SettingsAsync(ct);
                await monitor.ProfileAsync(ct);
            }, api, revoked),
            Loop("remote", config.RemotePollEvery, remote.RunOnceAsync, api, revoked),
            Loop("runs", config.FlushEvery, runs.ReportAsync, api, revoked),
            Loop("metrics", config.MetricsEvery, monitor.SampleAsync, api, revoked),
            StreamAsync(api, stream, revoked));
        if (api.Disconnected)
        {
            (identity with { AgentId = null, WorkspaceId = null }).Save(config);
            log.Write("the API refused this agent (revoked or expired): run `cm-agent login` again");
        }

        return 0;
    }

    private async Task Loop(string name, TimeSpan every, Func<CancellationToken, Task> work, ApiClient api, CancellationTokenSource cts,
        Action<string>? onFailure = null)
    {
        var failures = 0;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await work(cts.Token);
                failures = 0;
            }
            catch (Exception e)
            {
                if (cts.IsCancellationRequested) break; // stopping: whatever the request in flight ended with is not a failure
                // Only the daemon's own token means stopping; a timeout (also an OperationCanceledException) is a failure.
                failures++;
                log.Write($"{name} failed ({failures}): {e.GetType().Name} {e.Message}");
                onFailure?.Invoke(e.GetType().Name);
            }

            if (api.Disconnected) await cts.CancelAsync();
            await Wait(Backoff(every, failures), cts.Token);
        }
    }

    /// <summary>Ends the daemon when an update (or a rollback) asks for it by writing the stop-request file.</summary>
    private async Task StopRequestAsync(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(config.StopRequestPath))
            {
                TryDelete(config.StopRequestPath);
                log.Write("stop requested (an update)");
                await cts.CancelAsync();
                return;
            }

            await Wait(config.StopPollEvery, cts.Token);
        }
    }

    /// <summary>A request written before this daemon took its lock was meant for an earlier one; one written after is ours and stays.</summary>
    private void DropStaleStopRequest(DateTimeOffset started)
    {
        try
        {
            if (!File.Exists(config.StopRequestPath)) return;
            var at = DateTimeOffset.TryParse(File.ReadAllText(config.StopRequestPath), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var written) ? written : (DateTimeOffset?)null;
            if (at is null || at < started) File.Delete(config.StopRequestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private async Task StreamAsync(ApiClient api, Relay relay, CancellationTokenSource cts)
    {
        var failures = 0;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await foreach (var (name, data) in api.StreamAsync(cts.Token, () => relay.StreamState(PushStatus.Connected, 0, null)))
                {
                    failures = 0;
                    if (!await relay.OnStreamAsync(name, data, cts.Token))
                    {
                        log.Write("revoked from the web");
                        await cts.CancelAsync();
                        return;
                    }
                }

                relay.StreamState(PushStatus.Reconnecting, failures, "StreamClosed"); // the API ended it: reopen
            }
            catch (Exception e)
            {
                if (cts.IsCancellationRequested) return; // stopping, as above
                // A connect timeout is an OperationCanceledException too; it must not end the stream for good.
                failures++;
                log.Write($"stream failed ({failures}): {e.GetType().Name} {e.Message}");
                relay.StreamState(PushStatus.Reconnecting, failures, e.GetType().Name);
            }

            if (api.Disconnected) await cts.CancelAsync();
            await Wait(Backoff(TimeSpan.FromSeconds(1), failures), cts.Token);
        }
    }

    /// <summary>Doubles the wait per consecutive failure, up to the configured ceiling.</summary>
    internal TimeSpan Backoff(TimeSpan every, int failures) =>
        failures == 0 ? every : TimeSpan.FromTicks(Math.Min(config.RetryMax.Ticks, every.Ticks * (1L << Math.Min(failures, 16))));

    private async Task Wait(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, clock, ct);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>The exclusive lock, or null when another process holds it.</summary>
    public static FileStream? TryLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Starts the daemon (from <paramref name="binary"/>, else from this process's own binary) detached when no daemon holds the lock. Never throws: a hook must not fail.</summary>
    public static bool EnsureRunning(AgentConfig config, AgentLog log, string? binary = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            config.EnsureHome();
            using (var probe = TryLock(config.LockPath))
            {
                if (probe is null) return true;
            }

            var exe = binary ?? Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("daemon");
            using var child = Process.Start(info);
            return child is not null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            log.Write($"could not start the daemon: {e.Message}");
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "setsid")]
    private static partial int SetSid();
}
