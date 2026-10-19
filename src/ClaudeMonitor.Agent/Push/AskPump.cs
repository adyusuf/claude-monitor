using System.Net.Sockets;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// Carries asks from other machines into an idle Claude session (ADR-0008 §3f), run by the daemon every <see cref="AgentConfig.PushPollEvery"/>. For
/// each queued ask it picks a session that has an inbox, has been idle for <see cref="AgentConfig.AskIdleFor"/> and, when it can, works in the
/// project the question names, writes the envelope and reports "delivered" with the session. It tries at most two sessions, then reports
/// <c>no_idle_session</c>; push off or a machine whose own claim is strict reports <c>not_accepting</c>. On macOS and Linux the line is written
/// WITHOUT the session's key (so Claude Code holds it for the person at the terminal instead of delivering it at once in a bypass session); on Windows
/// the key is mandatory, so only a session whose last recorded permission mode is default or plan receives it. A delivered ask that does not show in
/// the transcript within <see cref="AgentConfig.AskConfirmWait"/> is reported not_accepting. The requester is told once that this daemon holds the ask
/// (<c>received</c>); an ask for which no session is idle is KEPT in the database, queued, and tried again on every pass until a session takes it or the
/// API expires it (it is not given up at once). The text is never logged.
/// </summary>
public sealed class AskPump(Func<AgentConfig> settings, LocalStore store, IInboxTransport transport, Func<string, AskStatusUpdate, CancellationToken, Task> report,
    Func<bool> strictClaim, TimeProvider clock, Func<DateTimeOffset, ISet<int>>? idlePids = null)
{
    public const int Attempts = 2;
    public static readonly string[] AcceptingModes = ["default", "plan"];
    private readonly Func<DateTimeOffset, ISet<int>> idle = idlePids ?? (now => IdleProcessIds(settings(), store, now));

    public async Task<int> PumpAsync(CancellationToken ct)
    {
        var config = settings();
        var now = clock.GetUtcNow();
        var delivered = 0;
        var accepting = config.InboxPushEnabled && config.AsksEnabled && !strictClaim();
        if (accepting) await AcknowledgeAsync(now, ct); // a machine that refuses asks says so below instead, once
        foreach (var ask in store.QueuedAsks(now))
        {
            if (!accepting)
            {
                await EndAsync(ask.Id, AskStatuses.NotAccepting, AskStatuses.NotAccepting, ct);
                continue;
            }

            if (await DeliverAsync(ask, config, now, ct)) delivered++; // no idle session now: the ask stays queued for the next pass (or the next session)
        }

        foreach (var ask in store.StaleQueuedAsks(now)) store.CloseAsk(ask.Id);
        foreach (var ask in store.UnconfirmedAsks().Where(a => a.DeliveredAt is { } at && now - at >= config.AskConfirmWait))
        {
            await EndAsync(ask.Id, AskStatuses.NotAccepting, "not_confirmed", ct);
        }

        store.ForgetAsks(now - TimeSpan.FromDays(1));
        return delivered;
    }

    /// <summary>The processes of the sessions that have been idle at least <see cref="AgentConfig.AskIdleFor"/>, in every config folder the hooks recorded.</summary>
    public static ISet<int> IdleProcessIds(AgentConfig config, LocalStore store, DateTimeOffset now)
    {
        var pids = new HashSet<int>();
        foreach (var dir in ClaudeSessions.ConfigDirs(config, store, now, TimeSpan.FromDays(1)))
        {
            foreach (var s in ClaudeSessions.Read(dir).Live)
            {
                if (s.Status == "idle" && s.StatusAt is { } at && now - at >= config.AskIdleFor) pids.Add(s.Pid);
            }
        }

        return pids;
    }

    /// <summary>Idle sessions with an inbox, those working in the question's project first, then the most recently seen.</summary>
    public IReadOnlyList<SessionInbox> Candidates(AskIn ask, AgentConfig config, DateTimeOffset now)
    {
        var idlePids = idle(now);
        return [.. store.Inboxes()
            .Where(i => store.PidOfSession(i.Session) is { } pid && idlePids.Contains(pid))
            .Where(i => Accepts(i))
            .OrderByDescending(i => ask.Project is not null && store.ProjectKeyOf(i.Session) == ask.Project)
            .ThenByDescending(i => i.SeenAt)];
    }

    /// <summary>On Windows only a session recorded in default or plan mode receives an ask (the auth line is mandatory there); elsewhere any session may be offered it, held for the person.</summary>
    private static bool Accepts(SessionInbox inbox) => !OperatingSystem.IsWindows() || (inbox.PermissionMode is { } mode && AcceptingModes.Contains(mode, StringComparer.Ordinal));

    private async Task<bool> DeliverAsync(AskIn ask, AgentConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var tried = 0;
        foreach (var inbox in Candidates(ask, config, now))
        {
            if (tried++ >= Attempts) break;
            try
            {
                var command = new LocalCommand(ask.Id, inbox.Session, "ask", null, ask.NotAfter);
                var token = OperatingSystem.IsWindows() ? inbox.Token : null; // never the key where it is optional: a held message is what an ask from another machine deserves
                await transport.SendAsync(inbox.Endpoint, InboxLines.For(command, token, AskEnvelopes.Build(ask, config.PushContentMax)), config.InboxConnectTimeout, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or SocketException or TimeoutException or InvalidOperationException
                                          or UnauthorizedAccessException or ArgumentException)
            {
                store.DropInbox(inbox.Session);
                store.Set(LocalStore.InboxErrorKey, e.GetType().Name);
                store.Set(LocalStore.InboxErrorAtKey, now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            if (!store.AskDeliveredTo(ask.Id, inbox.Session, now)) return true;
            store.Bump(PushStatus.OutCountKey);
            await ReportAsync(ask.Id, new AskStatusUpdate(AskStatuses.Delivered, SessionExternalId: inbox.Session, At: now), ct);
            return true;
        }

        return false;
    }

    /// <summary>Tells the requester, once per ask, that this daemon holds it; a report that did not get through is repeated on the next pass.</summary>
    private async Task AcknowledgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var ask in store.AsksToAcknowledge(now))
        {
            try
            {
                await report(ask.Id, new AskStatusUpdate(AskStatuses.Received, At: now), ct);
                store.MarkAskAcknowledged(ask.Id);
            }
            catch (Net.ApiException e) when (e.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                store.MarkAskAcknowledged(ask.Id); // the API no longer has it open: nothing to tell
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is Net.ApiException or HttpRequestException or TimeoutException)
            {
                return; // the API cannot be reached: the rest waits for the next pass
            }
        }
    }

    private async Task EndAsync(string id, string status, string error, CancellationToken ct)
    {
        store.CloseAsk(id);
        await ReportAsync(id, new AskStatusUpdate(status, error), ct);
    }

    private async Task ReportAsync(string id, AskStatusUpdate update, CancellationToken ct)
    {
        try
        {
            await report(id, update, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is Net.ApiException or HttpRequestException or TimeoutException)
        {
            // the API expires or finishes the ask itself; what happened here is not retried in a loop
        }
    }
}
