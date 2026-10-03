using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;

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
        if (!OperatingSystem.IsWindows()) _ = SetSid(); // leave the hook's process group: its end must not end us

        var identity = Identity.Load(config);
        if (!identity.Connected)
        {
            log.Write("not connected: run `cm-agent login --server <url>`");
            return 1;
        }

        using var store = new LocalStore(config.DatabasePath);
        using var http = ApiClient.CreateHttp(identity.Server!, handler);
        using var api = new ApiClient(http, Credentials.For(config));
        var relay = new Relay(config, store, api, clock);
        var tailer = new TranscriptTailer(config, store, clock);
        using var revoked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        log.Write($"daemon started, version {AgentConfig.Version}");
        await Task.WhenAll(
            Loop("flush", config.FlushEvery, async ct =>
            {
                tailer.RunOnce();
                await relay.PermissionsAsync(ct);
                await relay.UploadAsync(ct);
                await relay.ReportCommandsAsync(ct);
            }, api, revoked),
            Loop("heartbeat", config.HeartbeatEvery, api.HeartbeatAsync, api, revoked),
            Loop("settings", config.SettingsEvery, relay.SettingsAsync, api, revoked),
            StreamAsync(api, relay, revoked));
        if (api.Disconnected)
        {
            (identity with { AgentId = null, WorkspaceId = null }).Save(config);
            log.Write("the API refused this agent (revoked or expired): run `cm-agent login` again");
        }

        return 0;
    }

    private async Task Loop(string name, TimeSpan every, Func<CancellationToken, Task> work, ApiClient api, CancellationTokenSource cts)
    {
        var failures = 0;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await work(cts.Token);
                failures = 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures++;
                log.Write($"{name} failed ({failures}): {e.GetType().Name} {e.Message}");
            }

            if (api.Disconnected) await cts.CancelAsync();
            await Wait(Backoff(every, failures), cts.Token);
        }
    }

    private async Task StreamAsync(ApiClient api, Relay relay, CancellationTokenSource cts)
    {
        var failures = 0;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await foreach (var (name, data) in api.StreamAsync(cts.Token))
                {
                    failures = 0;
                    if (!await relay.OnStreamAsync(name, data, cts.Token))
                    {
                        log.Write("revoked from the web");
                        await cts.CancelAsync();
                        return;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures++;
                log.Write($"stream failed ({failures}): {e.GetType().Name} {e.Message}");
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

    /// <summary>Starts the daemon detached when no daemon holds the lock. Never throws: a hook must not fail.</summary>
    public static bool EnsureRunning(AgentConfig config, AgentLog log)
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

            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
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
