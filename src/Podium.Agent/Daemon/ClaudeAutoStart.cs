using System.Globalization;
using Podium.Agent.Config;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

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

    /// <summary>The project key the mode "always" keeps a session running in (workspace-tagged like <see cref="ModeKey"/>).</summary>
    public const string ProjectKey = "settings.claude_auto_project";

    public const string StartsKey = "claude.auto.starts";
    public const string BackoffPrefix = "claude.auto.backoff.";

    private readonly Dictionary<string, DateTimeOffset> unserved = [];

    private DateTimeOffset lastReconcile = DateTimeOffset.MinValue;

    public bool Enabled(AgentConfig config) =>
        !config.ServiceMode && !strictClaim() && WorkspaceSettings.Get(config, store, ModeKey) is ClaudeAutoModes.OnMessage or ClaudeAutoModes.Always;

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

        await ReconcileAsync(config, now, ct);
        if (await KeepOneRunningAsync(config, now, ct)) return 1;
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
            var outcome = await control.StartAsync(project, null, ct, reason: "auto:" + ClaudeAutoModes.OnMessage);
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

    /// <summary>Asks Claude Code which of our sessions still run, at most once a minute, so one that ended on its own stops counting.</summary>
    private async Task ReconcileAsync(AgentConfig config, DateTimeOffset now, CancellationToken ct)
    {
        if (now - lastReconcile < config.ClaudeAutoReconcileEvery) return;
        lastReconcile = now;
        if (await control.ReconcileAsync(config.ClaudeAutoWait * 4, ct) is > 0 and var ended) log.Write($"{ended} Claude session(s) started here ended on their own");
    }

    /// <summary>The mode "always": one session is kept running in the project the owner named, within the same budget and backoff as every other start. True when it started one.</summary>
    private async Task<bool> KeepOneRunningAsync(AgentConfig config, DateTimeOffset now, CancellationToken ct)
    {
        if (WorkspaceSettings.Get(config, store, ModeKey) != ClaudeAutoModes.Always) return false;
        if (WorkspaceSettings.Get(config, store, ProjectKey) is not { Length: > 0 } project) return false;
        if (control.Running().Any(w => w.ProjectKey == project)) return false;
        if (BackoffUntil(project) > now || StartsSince(now - TimeSpan.FromHours(1)).Count >= config.ClaudeAutoStartsPerHour) return false;
        var outcome = await control.StartAsync(project, null, ct, reason: "auto:" + ClaudeAutoModes.Always);
        if (outcome.Succeeded)
        {
            RecordStart(now);
            log.Write($"started Claude session {outcome.SessionRef} by itself to keep one running (automatic mode {ClaudeAutoModes.Always})");
            return true;
        }

        store.Set(BackoffPrefix + project, (now + config.ClaudeAutoBackoff).ToString("O", CultureInfo.InvariantCulture));
        log.Write($"automatic Claude start did not happen: {outcome.Text}");
        return false;
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
