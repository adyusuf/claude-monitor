using System.Globalization;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The automatic mode <c>on_message</c> (ADR-0014 P3): when an ask from another machine names a project and no session on this machine is idle to take it, the daemon starts ONE Claude session
/// in that project with <see cref="ClaudeControl"/> (the same fixed command as a start from the web, permission mode manual, no prompt), and the ask is then delivered to it by the usual pump once it is idle.
/// Off unless the machine's owner turned it on on the web (the API tells the agent only while their switch is on); never on a service agent or a machine that claims production or database.
/// Bounded three ways: an ask must have gone unserved for <see cref="AgentConfig.ClaudeAutoWait"/> first, at most <see cref="AgentConfig.ClaudeAutoStartsPerHour"/> sessions are started by itself per hour
/// (kept in the local database, so a restart does not reset it), and a project that has a session this agent started and has not stopped gets no second one; after a refused or failed start that
/// project waits <see cref="AgentConfig.ClaudeAutoBackoff"/>. The text of an ask is never read here, logged or passed on.
/// </summary>
public sealed class ClaudeAutoStart(Func<AgentConfig> settings, LocalStore store, ClaudeControl control, Func<AskIn, DateTimeOffset, bool> hasIdleSession,
    Func<bool> strictClaim, AgentLog log, TimeProvider clock)
{
    /// <summary>The workspace-tagged switch the settings pass keeps: "on_message" or "off".</summary>
    public const string ModeKey = "settings.claude_auto_mode";

    public const string StartsKey = "claude.auto.starts";
    public const string BackoffPrefix = "claude.auto.backoff.";

    private readonly Dictionary<string, DateTimeOffset> unserved = [];

    public bool Enabled(AgentConfig config) =>
        !config.ServiceMode && !strictClaim() && WorkspaceSettings.Get(config, store, ModeKey) == ClaudeAutoModes.OnMessage;

    /// <summary>One pass: returns how many sessions it started (0 or 1).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var config = settings();
        var now = clock.GetUtcNow();
        if (!Enabled(config))
        {
            unserved.Clear();
            return 0;
        }

        var waiting = store.QueuedAsks(now).Where(a => a.Project is { Length: > 0 }).ToList();
        foreach (var gone in unserved.Keys.Where(id => waiting.All(a => a.Id != id)).ToList()) unserved.Remove(gone);
        foreach (var ask in waiting)
        {
            if (hasIdleSession(ask, now)) unserved.Remove(ask.Id);
            else unserved.TryAdd(ask.Id, now);
        }

        var due = waiting.Where(a => unserved.TryGetValue(a.Id, out var since) && now - since >= config.ClaudeAutoWait).Select(a => a.Project!).Distinct(StringComparer.Ordinal).ToList();
        foreach (var project in due)
        {
            if (control.Running().Any(w => w.ProjectKey == project)) continue; // it has one: that one takes the ask when it is idle
            if (BackoffUntil(project) > now) continue;
            if (StartsSince(now - TimeSpan.FromHours(1)).Count >= config.ClaudeAutoStartsPerHour) return 0;
            var outcome = await control.StartAsync(project, null, ct);
            if (outcome.Succeeded)
            {
                RecordStart(now);
                log.Write($"started Claude session {outcome.SessionRef} by itself for an ask nobody could take (automatic mode {ClaudeAutoModes.OnMessage})");
                return 1;
            }

            store.Set(BackoffPrefix + project, (now + config.ClaudeAutoBackoff).ToString("O", CultureInfo.InvariantCulture));
            log.Write($"automatic Claude start did not happen: {outcome.Text}");
        }

        return 0;
    }

    private DateTimeOffset BackoffUntil(string project) =>
        DateTimeOffset.TryParse(store.Get(BackoffPrefix + project), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until) ? until : DateTimeOffset.MinValue;

    private List<DateTimeOffset> StartsSince(DateTimeOffset since) =>
        [.. (store.Get(StartsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue)
            .Where(at => at > since)];

    private void RecordStart(DateTimeOffset now) =>
        store.Set(StartsKey, string.Join(',', StartsSince(now - TimeSpan.FromHours(1)).Append(now).Select(t => t.ToString("O", CultureInfo.InvariantCulture))));
}
