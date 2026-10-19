using System.Globalization;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent;

/// <summary>The commands a person runs by hand: install, restart and status.</summary>
public static partial class Cli
{
    /// <summary>"aipod install [--stop-wait N] [--push on|off]": the values are saved, so the hooks written now and every later run agree on it. The first install, with no push choice saved yet, turns the inbox push on.</summary>
    public static int Install(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr, string sourceBinary,
        Func<string, string[], int>? run = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);
        config = SavedSettings.Apply(config);
        if (Array.IndexOf(args, "--stop-wait") >= 0)
        {
            var text = Option(args, "--stop-wait") ?? "";
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > AgentConfig.WaitMaxSeconds)
            {
                stderr.WriteLine($"--stop-wait takes whole seconds from 0 to {AgentConfig.WaitMaxSeconds}, not \"{text}\".");
                return 2;
            }

            config = SavedSettings.SaveStopWait(config, seconds);
        }

        if (Array.IndexOf(args, "--push") >= 0)
        {
            var value = Option(args, "--push");
            if (value != "off" && !PushTransports.Enables(value))
            {
                stderr.WriteLine($"--push takes on, off, inbox or channel, not \"{value}\".");
                return 2;
            }

            config = SavedSettings.SavePush(config, value != "off", value == "off" ? null : PushTransports.Normalize(value));
        }

        if (Array.IndexOf(args, "--push-format") >= 0)
        {
            var format = Option(args, "--push-format");
            if (!InboxFormats.IsValid(format))
            {
                stderr.WriteLine($"--push-format takes full or short, not \"{format}\".");
                return 2;
            }

            config = SavedSettings.SaveInboxFormat(config, format!);
        }

        if (Array.IndexOf(args, "--push") < 0 && !config.PushFromEnvironment && Identity.Peek(config)?.Push is null)
        {
            // The first install turns the push on, through the inbox (the maintainer, 17/10/2026): it needs no start-up flag, so it works in the desktop app
            // and VS Code too, where a channel is never loaded and the message only arrives at the next prompt. --push channel picks the channel instead.
            // Once anything is saved (--push off, config push off, or an earlier install) it is kept as chosen.
            config = SavedSettings.SavePush(config, true, PushTransports.Inbox);
        }

        return new PluginInstaller(config, stdout, run).Install(sourceBinary);
    }

    /// <summary>A hook never fails its session: whatever happens, exit 0 (project rule).</summary>
    public static async Task<int> HookAsync(string hookEvent, AgentConfig config, TextReader stdin, TextWriter stdout,
        TextWriter stderr, TimeProvider clock, AgentLog log)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            config.EnsureHome();
            var input = await stdin.ReadToEndAsync();
            string? output;
            using (var store = new LocalStore(config.DatabasePath))
            {
                output = await new HookRunner(config, store, clock).RunAsync(hookEvent, input, CancellationToken.None);
            }

            if (Identity.Load(config).Connected) DaemonHost.EnsureRunning(config, log);
            if (output is not null) await stdout.WriteAsync(output);
        }
        catch (Exception e)
        {
            await stderr.WriteLineAsync($"aipod hook {hookEvent}: {e.GetType().Name}: {e.Message}");
            log.Write($"hook {hookEvent} failed: {e.GetType().Name} {e.Message}");
        }

        return 0;
    }

    /// <summary>"aipod restart": hands the daemon over to this binary (a new version was just put in place). Nothing to do while not connected.</summary>
    public static async Task<int> RestartAsync(AgentConfig config, AgentLog log, TimeProvider clock, TextWriter stdout, Update.IDaemonControl? control = null)
    {
        if (!Identity.Load(config).Connected)
        {
            await stdout.WriteLineAsync("Not connected: there is no daemon to restart.");
            return 0;
        }

        var daemon = control ?? new Update.DaemonControl(config, log, clock);
        if (daemon.IsRunning() && !await daemon.StopAsync(config.UpdateStopWait, CancellationToken.None) && !daemon.Kill())
        {
            await stdout.WriteLineAsync("The daemon did not stop; it keeps running its older version until it ends.");
            return 1;
        }

        await stdout.WriteLineAsync(daemon.Start(config.BinaryPath) ? "The daemon was restarted." : "The daemon could not be started.");
        return 0;
    }

    public static async Task<int> StatusAsync(AgentConfig config, TextWriter stdout, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var identity = Identity.Load(config);
        using var store = new LocalStore(config.DatabasePath);
        using var probe = DaemonHost.TryLock(config.LockPath);
        await stdout.WriteLineAsync($"home: {config.Home}");
        await stdout.WriteLineAsync(identity.Connected ? $"connected: {identity.Server}" : "not connected (aipod login --server <url>)");
        await stdout.WriteLineAsync($"daemon: {(probe is null ? "running" : "not running")}");
        await stdout.WriteLineAsync($"events waiting: {store.OutboxCount()}");
        var (commands, soonest) = store.WaitingCommands((clock ?? TimeProvider.System).GetUtcNow());
        await stdout.WriteLineAsync(soonest is { } expires
            ? $"commands waiting: {commands} (oldest expires {expires.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)})"
            : "commands waiting: 0");
        await stdout.WriteLineAsync($"stop wait: {(int)config.StopWait.TotalSeconds} s");
        await stdout.WriteLineAsync($"remote runs: {ExecPolicyLoader.Describe(ExecPolicyLoader.Load(config, identity.ExecLevel))}{(config.ServiceMode ? " (service)" : "")}");
        await stdout.WriteLineAsync($"Claude from the web: {new Daemon.ClaudeControl(config, new Daemon.SystemClaudeCli(), clock ?? TimeProvider.System).Running().Count} session(s) started here running; automatic start: {Daemon.WorkspaceSettings.Get(config, store, Daemon.ClaudeAutoStart.ModeKey) ?? "off"}");
        await stdout.WriteLineAsync(Approvals.ApproversStore.Describe(Approvals.ApproversStore.Load(config)));
        foreach (var line in PushStatus.Describe(config, store, (clock ?? TimeProvider.System).GetUtcNow(), null, probe is null)) await stdout.WriteLineAsync(line);
        foreach (var line in UpdateCommand.Describe(config, store)) await stdout.WriteLineAsync(line);
        foreach (var line in ClaudeUpdateCommand.Describe(config, store, clock ?? TimeProvider.System)) await stdout.WriteLineAsync(line);
        await stdout.WriteLineAsync($"version: {AgentConfig.Version}");
        return identity.Connected ? 0 : 1;
    }
}
