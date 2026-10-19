using System.Globalization;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using Microsoft.Data.Sqlite;

namespace Aipod.Tray;

/// <summary>What the tray shows about the agent on this machine, read from its files; nothing here talks to the network.</summary>
public sealed record TrayStatus(string Version, bool Connected, bool DaemonRunning, string? Server, string Headline, IReadOnlyList<string> Lines,
    string? UpdateAvailable, long Incoming, long Outgoing)
{
    /// <summary>The text under the pointer: short enough for the Windows limit of 127 characters, with the version, the state and the update.</summary>
    public string Tooltip()
    {
        var text = Strings.T(Msg.TrayTooltip, Version, Headline) + (UpdateAvailable is { } update ? Strings.T(Msg.TrayTooltipUpdate, update) : "");
        return text.Length <= 120 ? text : text[..119] + "…";
    }
}

/// <summary>Reads the agent's identity, database and update state into a <see cref="TrayStatus"/>. A file that cannot be read becomes a sentence, never an exception.</summary>
public sealed class StatusReader(AgentConfig config, TimeProvider clock)
{
    /// <summary>Only the two message counters, cheap enough to read several times a second for the lights; (0, 0) when the database is not there or cannot be read.</summary>
    public (long Incoming, long Outgoing) ReadCounters()
    {
        if (!File.Exists(config.DatabasePath)) return (0, 0);
        try
        {
            using var store = new LocalStore(config.DatabasePath);
            return (store.Count(PushStatus.InCountKey), store.Count(PushStatus.OutCountKey));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    public TrayStatus Read()
    {
        var identity = Identity.Peek(config);
        var connected = identity?.Connected == true;
        var running = Directory.Exists(config.Home) && PushStatus.DaemonRunning(config);
        var update = UpdateState.Load(config).Available;
        var server = identity?.Server;
        if (!File.Exists(config.DatabasePath)) return Make(AgentConfig.Version, connected, running, server, Strings.T(connected ? Msg.TrayHlWaiting : Msg.TrayHlNotConnected), [], update, 0, 0);
        try
        {
            using var store = new LocalStore(config.DatabasePath);
            var lines = new List<string>(PushStatus.Describe(config, store, clock.GetUtcNow(), null, running));
            lines.AddRange(UpdateCommand.Describe(config, store));
            var version = store.Get(UpdateHealth.VersionKey) is { Length: > 0 } daemonVersion ? daemonVersion : AgentConfig.Version;
            return Make(version, connected, running, server, Headline(store, connected, running), lines, update, store.Count(PushStatus.InCountKey), store.Count(PushStatus.OutCountKey));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Make(AgentConfig.Version, connected, running, server, Strings.T(Msg.TrayHlCannotRead, e.GetType().Name), [], update, 0, 0);
        }
    }

    private static TrayStatus Make(string version, bool connected, bool running, string? server, string headline, IReadOnlyList<string> lines, string? update, long incoming, long outgoing) =>
        new(version, connected, running, server, headline, lines, update, incoming, outgoing);

    private static string Headline(LocalStore store, bool connected, bool running)
    {
        if (store.Get(AuthRefusal.ReasonKey) is { Length: > 0 } && !connected) return Strings.T(Msg.TrayHlRefused);
        if (!connected) return Strings.T(Msg.TrayHlNotConnected);
        if (!running) return Strings.T(Msg.TrayHlNotRunning);
        return store.Get(PushStatus.StreamStateKey) switch
        {
            PushStatus.Connected => Strings.T(Msg.TrayHlConnected),
            PushStatus.Reconnecting => Strings.T(Msg.TrayHlReconnecting),
            _ => Strings.T(Msg.TrayHlStreamNotOpen),
        };
    }

    /// <summary>"3 s", "5 min": how old a reading is, for the window's footer.</summary>
    public static string Age(TimeSpan span) =>
        span.TotalSeconds < 120 ? Strings.T(Msg.TrayAgeSeconds, Math.Max(0, (int)span.TotalSeconds)) : Strings.T(Msg.TrayAgeMinutes, (int)span.TotalMinutes);
}
