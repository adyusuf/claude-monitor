using System.Globalization;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// The agent's own account of the path a message takes (ADR-0003): the event outbox, the stream from the web and the
/// push into the session. The daemon and the MCP process write these keys into the local database; `monitor_status` and
/// `cm-agent status` read them. Only ids, counts, times and exception type names: never message content.
/// </summary>
public static class PushStatus
{
    public const string StreamStateKey = "stream.state";
    public const string StreamStateAtKey = "stream.state_at";
    public const string StreamFailuresKey = "stream.failures";
    public const string StreamErrorKey = "stream.last_error";
    public const string StreamCommandKey = "stream.last_command_id";
    public const string StreamCommandAtKey = "stream.last_command_at";
    public const string UploadAtKey = "relay.last_upload_at";
    public const string UploadErrorKey = "relay.last_error";
    public const string UploadErrorAtKey = "relay.last_error_at";
    public const string LastPushKey = "push.last_id";
    public const string LastPushAtKey = "push.last_at";

    public const string Connected = "connected";
    public const string Reconnecting = "reconnecting";

    /// <summary>The lines `monitor_status` appends after its original sentence, and `cm-agent status` prints.</summary>
    public static IReadOnlyList<string> Describe(AgentConfig config, LocalStore store, DateTimeOffset now, string? session, bool daemonRunning)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        var lines = new List<string>();
        var waiting = store.OutboxCount();
        var oldest = store.OutboxOldest();
        var upload = Time(store.Get(UploadAtKey));
        lines.Add($"Events queued locally: {waiting}" + (oldest is { } o ? $" (oldest {Age(now - o)})" : "") + "; last upload " +
                  (upload is { } u ? Age(now - u) + " ago" : "never") + ".");
        if (store.Get(UploadErrorKey) is { Length: > 0 } uploadError)
        {
            lines.Add($"Last upload failure: {uploadError} at {Clock(Time(store.Get(UploadErrorAtKey)))}.");
        }

        lines.Add(daemonRunning ? Stream(store, now) : "Web stream: the agent daemon is not running.");
        lines.Add(Push(config, store, now, session));
        return lines;
    }

    /// <summary>True when a daemon holds the lock file. The caller must not hold the lock itself.</summary>
    public static bool DaemonRunning(AgentConfig config)
    {
        using var probe = DaemonHost.TryLock(config.LockPath);
        return probe is null;
    }

    private static string Stream(LocalStore store, DateTimeOffset now)
    {
        var last = store.Get(StreamCommandKey) is { Length: > 0 } id ? $"; last message id {id} ({Age(now - (Time(store.Get(StreamCommandAtKey)) ?? now))} ago)" : "; no message received yet";
        return store.Get(StreamStateKey) switch
        {
            Connected => $"Web stream: connected since {Clock(Time(store.Get(StreamStateAtKey)))}{last}.",
            Reconnecting => $"Web stream: reconnecting (failure {store.Get(StreamFailuresKey)}: {store.Get(StreamErrorKey)}){last}.",
            _ => $"Web stream: not opened yet{last}.",
        };
    }

    private static string Push(AgentConfig config, LocalStore store, DateTimeOffset now, string? session)
    {
        if (!config.PushEnabled) return "Push into the session: off (cm-agent install --push on); web messages arrive at the next prompt.";
        var (queued, _) = store.WaitingCommands(now);
        var unconfirmed = store.UnconfirmedPushes().Count;
        var scope = config.PushScope == PushScopes.Machine ? "every session on this machine" : $"this session ({session ?? "not bound yet"})";
        var last = store.Get(LastPushKey) is { Length: > 0 } id ? $"last pushed {id} at {Clock(Time(store.Get(LastPushAtKey)))}" : "nothing pushed yet";
        return $"Push into the session: on for {scope}; {last}; {unconfirmed} pushed but not yet seen in the transcript; " +
               $"{store.PushedTotal()} confirmed; {queued} waiting for a hook.";
    }

    private static DateTimeOffset? Time(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static string Clock(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "?";

    private static string Age(TimeSpan span) =>
        span < TimeSpan.Zero ? "0 s" : span.TotalSeconds < 120 ? $"{(int)span.TotalSeconds} s" : span.TotalMinutes < 120 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalHours} h";
}
