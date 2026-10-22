using System.Globalization;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;

namespace Podium.Agent.Push;

/// <summary>
/// The agent's own account of the path a message takes (ADR-0003): the event outbox, the stream from the web and the
/// push into the session. The daemon and the MCP process write these keys into the local database; `aipod_status` and
/// `aipod status` read them. Only ids, counts, times and exception type names: never message content.
/// </summary>
public static class PushStatus
{
    public const string StreamStateKey = "stream.state";
    public const string StreamStateAtKey = "stream.state_at";
    public const string StreamFailuresKey = "stream.failures";
    public const string StreamErrorKey = "stream.last_error";
    public const string StreamCommandKey = "stream.last_command_id";

    /// <summary>Counters the tray's two lights watch (orange: a message arrived here; green: a message left or reached a session).</summary>
    public const string InCountKey = "leds.in";

    public const string OutCountKey = "leds.out";
    public const string StreamCommandAtKey = "stream.last_command_at";
    public const string UploadAtKey = "relay.last_upload_at";
    public const string UploadErrorKey = "relay.last_error";
    public const string UploadErrorAtKey = "relay.last_error_at";
    public const string LastPushKey = "push.last_id";
    public const string LastPushAtKey = "push.last_at";

    public const string Connected = "connected";
    public const string Reconnecting = "reconnecting";

    /// <summary>The lines `aipod_status` appends after its original sentence, and `aipod status` prints.</summary>
    public static IReadOnlyList<string> Describe(AgentConfig config, LocalStore store, DateTimeOffset now, string? session, bool daemonRunning)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        var lines = new List<string>();
        var waiting = store.OutboxCount();
        var oldest = store.OutboxOldest();
        var upload = Time(store.Get(UploadAtKey));
        lines.Add(Strings.T(Msg.StEventsQueued, waiting, oldest is { } o ? Strings.T(Msg.StOldest, Age(now - o)) : "",
            upload is { } u ? Strings.T(Msg.StAgo, Age(now - u)) : Strings.T(Msg.StNever)));
        if (store.Get(UploadErrorKey) is { Length: > 0 } uploadError)
        {
            lines.Add(Strings.T(Msg.StUploadFailure, uploadError, Clock(Time(store.Get(UploadErrorAtKey)))));
        }

        if (store.Get(Auth.AuthRefusal.ReasonKey) is { Length: > 0 } refused && Auth.Identity.Peek(config)?.Connected != true)
        {
            lines.Add(Strings.T(Msg.StConnection, Auth.AuthRefusal.Describe(refused), Clock(Time(store.Get(Auth.AuthRefusal.AtKey)))));
        }

        lines.Add(daemonRunning ? Stream(store, now) : Strings.T(Msg.StDaemonDown));
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
        var last = store.Get(StreamCommandKey) is { Length: > 0 } id ? Strings.T(Msg.StLastMessage, id, Age(now - (Time(store.Get(StreamCommandAtKey)) ?? now))) : Strings.T(Msg.StNoMessage);
        return store.Get(StreamStateKey) switch
        {
            Connected => Strings.T(Msg.StStreamConnected, Clock(Time(store.Get(StreamStateAtKey))), last),
            Reconnecting => Strings.T(Msg.StStreamReconnecting, store.Get(StreamFailuresKey), store.Get(StreamErrorKey), last),
            _ => Strings.T(Msg.StStreamNotOpen, last),
        };
    }

    private static string Push(AgentConfig config, LocalStore store, DateTimeOffset now, string? session)
    {
        if (!config.PushEnabled) return Strings.T(Msg.StPushOff);
        if (config.InboxPushEnabled) return Inbox(store, now, session);
        var (queued, _) = store.WaitingCommands(now);
        var unconfirmed = store.UnconfirmedPushes().Count;
        var scope = config.PushScope == PushScopes.Machine ? Strings.T(Msg.StScopeMachine) : Strings.T(Msg.StScopeSession, session ?? Strings.T(Msg.StNotBound));
        var last = store.Get(LastPushKey) is { Length: > 0 } id ? Strings.T(Msg.StLastPushed, id, Clock(Time(store.Get(LastPushAtKey)))) : Strings.T(Msg.StNothingPushed);
        return Strings.T(Msg.StPushOn, scope, last, unconfirmed, store.PushedTotal(), queued);
    }

    /// <summary>The inbox transport (ADR-0007): whether this session's socket is known, what was pushed and what the last failure was. No content, no key.</summary>
    private static string Inbox(LocalStore store, DateTimeOffset now, string? session)
    {
        var (queued, _) = store.WaitingCommands(now);
        var known = session is not null && store.InboxOf(session) is not null;
        var where = Strings.T(session is null ? Msg.StInboxNotBound : known ? Msg.StInboxKnown : Msg.StInboxUnknown);
        var last = store.Get(LastPushKey) is { Length: > 0 } id ? Strings.T(Msg.StLastPushed, id, Clock(Time(store.Get(LastPushAtKey)))) : Strings.T(Msg.StNothingPushed);
        var failure = store.Get(LocalStore.InboxErrorKey) is { Length: > 0 } error ? Strings.T(Msg.StInboxFailure, error, Clock(Time(store.Get(LocalStore.InboxErrorAtKey)))) : "";
        var (read, unread) = store.ShortNotices();
        var notices = read + unread > 0 ? Strings.T(Msg.StNotices, read, unread) : "";
        return Strings.T(Msg.StInboxLine, where, last, store.UnconfirmedPushes().Count, store.PushedTotal(), queued, notices, failure);
    }

    private static DateTimeOffset? Time(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static string Clock(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "?";

    private static string Age(TimeSpan span) =>
        span < TimeSpan.Zero ? Strings.T(Msg.TrayAgeSeconds, 0) : span.TotalSeconds < 120 ? Strings.T(Msg.TrayAgeSeconds, (int)span.TotalSeconds) : span.TotalMinutes < 120 ? Strings.T(Msg.TrayAgeMinutes, (int)span.TotalMinutes) : Strings.T(Msg.StAgeHours, (int)span.TotalHours);
}
