using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
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
        using var machinesStore = new LocalStore(config.DatabasePath);
        var machines = new MachineCache(config, machinesStore, api, clock);
        var executor = new RunExecutor(config, clock, log, run => ExecGuard.Check(run, monitor.Policy(), AgentConfig.Os, config.Home),
            ExecPolicyLoader.RunningAsRoot);
        using var runs = new RunRelay(config, execStore, api, clock, executor, log);
        runs.Recover();
        ScriptScratch.Sweep(config.Home, _ => false); // no run is running yet: whatever a crash left in the scratch folders goes
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
        using var claudeStore = new LocalStore(config.DatabasePath);
        using var inboxStore = new LocalStore(config.DatabasePath);
        var inbox = new InboxPump(new LiveSettings(config, clock).Current, inboxStore, new SocketInboxTransport(), clock);
        var claudeUpdates = new ClaudeUpdater(config, claudeStore, new SystemProcessRunner(), new SystemNotifier(new SystemProcessRunner(), config.UpdateProbeTimeout), log, clock);
        log.Write($"daemon started, version {AgentConfig.Version}");
        await Task.WhenAll(
            Loop("claude-update", config.UpdatePollEvery, claudeUpdates.RunAsync, api, revoked),
            StopRequestAsync(revoked),
            PluginWatch.RunAsync(config, log, revoked, PluginWatch.DataExists(config)),
            Loop("update", config.UpdatePollEvery, updates.RunAsync, api, revoked),
            Loop("flush", config.FlushEvery, async ct =>
            {
                tailer.RunOnce();
                await relay.PermissionsAsync(ct);
                await relay.UploadAsync(ct);
                await relay.ReportCommandsAsync(ct);
            }, api, revoked, error => relay.UploadOutcome(error)),
            Loop("inbox", config.PushPollEvery, ct => inbox.PumpAsync(ct), api, revoked),
            Loop("heartbeat", config.HeartbeatEvery, beat.HeartbeatAsync, api, revoked, beat.HeartbeatFailed),
            Loop("settings", config.SettingsEvery, async ct =>
            {
                await settings.SettingsAsync(ct);
                await monitor.ProfileAsync(ct);
            }, api, revoked),
            Loop("remote", config.RemotePollEvery, remote.RunOnceAsync, api, revoked),
            Loop("runs", config.FlushEvery, runs.ReportAsync, api, revoked),
            Loop("metrics", config.MetricsEvery, monitor.SampleAsync, api, revoked),
            Loop("machines", config.MachineCacheEvery, machines.RefreshAsync, api, revoked),
            StreamAsync(api, stream, revoked));
        if (api.Disconnected)
        {
            (identity with { AgentId = null, WorkspaceId = null }).Save(config);
            log.Write("the API refused this agent (revoked or expired): run `cm-agent login` again");
        }

        return 0;
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
            Scrub(info);
            using var child = Process.Start(info);
            return child is not null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            log.Write($"could not start the daemon: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The daemon outlives the session whose hook started it, and reads every session's socket and key from the database: it must not also carry
    /// the starting session's own in its environment.
    /// </summary>
    public static void Scrub(ProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.Environment.Remove("CLAUDE_CODE_MESSAGING_TOKEN");
        info.Environment.Remove("CLAUDE_CODE_MESSAGING_SOCKET");
    }

    /// <summary>Leaves the process group of whoever started this process, so that its end does not end us.</summary>
    public static void Detach()
    {
        if (!OperatingSystem.IsWindows()) _ = SetSid();
    }

    [LibraryImport("libc", EntryPoint = "setsid")]
    private static partial int SetSid();
}
