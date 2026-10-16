using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The daemon's machine reports (ADR-0005): its profile (exec level, service mode) and a metric sample every
/// <see cref="AgentConfig.MetricsEvery"/>, with the alerts the evaluator opens or resolves. It only reports; it never acts on
/// an alert. Samples and alerts the API did not take are kept (bounded) and sent with the next pass. An API that does not
/// know these calls (404) switches the reports off until the daemon restarts.
/// </summary>
public sealed class MachineMonitor(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock, IMetricsSource source,
    Func<ExecPolicy>? policySource = null, Func<(ClaudePresence? Claude, IReadOnlyList<ToolInfo> Tools)>? factsSource = null)
{
    public const string ThresholdsKey = "settings.alert_thresholds";
    public const string RemoteRunsKey = "settings.remote_runs";
    public const int KeepSamples = 60;
    private static readonly AlertThresholds Defaults = new(90, 90, 90, 300);
    private readonly AlertEvaluator evaluator = new(Defaults);
    private readonly List<MetricSample> samples = [];
    private readonly List<AlertReport> alerts = [];
    private bool unsupported;
    private string? lastSent;
    private (ClaudePresence? Claude, IReadOnlyList<ToolInfo> Tools)? facts;
    private DateTimeOffset factsAt;

    /// <summary>The exec level this agent allows now: the admin-owned file in service mode, agent.json otherwise (with the role claim, ADR-0008).</summary>
    public ExecPolicy Policy()
    {
        if (policySource is not null) return policySource();
        var saved = Identity.Peek(config);
        return ExecPolicyLoader.Load(config, saved?.ExecLevel, saved?.ClaimedEnvironment, saved?.ClaimedFunction);
    }

    /// <summary>
    /// Sends the profile when it differs from the last one the API took (the facts change rarely, the session counts often): the
    /// exec level, the role claim, Claude presence, the tool inventory and the capabilities (none yet; the field tells a new agent from
    /// an old one). The inventory and the Claude version are probed again every <see cref="AgentConfig.PresenceProbeEvery"/>.
    /// </summary>
    public async Task ProfileAsync(CancellationToken ct)
    {
        if (unsupported) return;
        var policy = Policy();
        var level = ExecPolicyLoader.RunningAsRoot() ? ExecLevels.Off
            : policy.Level == ExecLevels.Shell && policy.HasCeiling ? ExecLevels.Argv : policy.Level;
        var (claude, tools) = Facts();
        var profile = new AgentProfile(level, config.ServiceMode, RuntimeInformation.OSDescription, Environment.UserName, config.ExecMaxConcurrent,
            policy.ClaimedEnvironment, policy.ClaimedFunction, claude, ToolInventory.Interpreters(tools), tools, []);
        var json = System.Text.Json.JsonSerializer.Serialize(profile with { Claude = claude is null ? null : claude with { SeenAt = default } }, ApiClient.Json);
        if (json == lastSent) return;
        if (await Guard(() => api.ProfileAsync(profile, ct))) lastSent = json;
    }

    private (ClaudePresence? Claude, IReadOnlyList<ToolInfo> Tools) Facts()
    {
        var now = clock.GetUtcNow();
        if (facts is { } cached && now - factsAt < config.PresenceProbeEvery && now >= factsAt)
        {
            // the sessions change often and cost one folder read; the inventory and the version are kept for the probe interval
            var (live, idle) = PresenceProbe.Sessions(config, store, now);
            return (cached.Claude is { } c ? c with { SessionsLive = live, SessionsIdle = idle, SeenAt = now } : null, cached.Tools);
        }

        var fresh = factsSource?.Invoke() ?? (PresenceProbe.Probe(config, store, real => ToolInventory.FirstLine(new Update.SystemProcessRunner().Run(real, ["--version"], config.ToolVersionTimeout).Output), now),
            ToolInventory.Live(config, AgentConfig.Os));
        facts = fresh;
        factsAt = now;
        return fresh;
    }

    public async Task SampleAsync(CancellationToken ct)
    {
        if (unsupported) return;
        if (Thresholds() is { } t) evaluator.Update(t);
        if (source.Sample(clock.GetUtcNow()) is { } sample)
        {
            samples.Add(sample);
            alerts.AddRange(evaluator.Observe(sample));
            if (samples.Count > KeepSamples) samples.RemoveRange(0, samples.Count - KeepSamples);
        }

        if (samples.Count > 0 && await Guard(() => api.MetricsAsync(new MetricsReport([.. samples]), ct))) samples.Clear();
        if (alerts.Count > 0 && await Guard(() => api.AlertsAsync([.. alerts], ct))) alerts.Clear();
    }

    /// <summary>Keeps what the settings pass learned: the alert thresholds and the workspace switch.</summary>
    public static void Remember(LocalStore store, AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.SetMany(Entries(settings));
    }

    /// <summary>What <see cref="Remember"/> keeps, as key/value pairs: a settings pass writes them in one transaction with its other keys.</summary>
    public static List<(string Key, string Value)> Entries(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<(string Key, string Value)> entries = [(RemoteRunsKey, settings.RemoteRuns ? "true" : "false")];
        if (settings.Alerts is { } a)
        {
            entries.Add((ThresholdsKey, string.Join(',', new[] { a.CpuPct, a.MemoryPct, a.DiskPct, a.SustainSeconds }
                .Select(v => v.ToString(CultureInfo.InvariantCulture)))));
        }

        return entries;
    }

    private AlertThresholds? Thresholds()
    {
        var parts = store.Get(ThresholdsKey)?.Split(',');
        if (parts is not { Length: 4 }) return null;
        var v = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return null;
        }

        return new AlertThresholds(v[0], v[1], v[2], v[3]);
    }

    /// <summary>True when the call went through; an older API's 404 switches the reports off, other failures are retried later.</summary>
    private async Task<bool> Guard(Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            unsupported = true;
            return false;
        }
    }
}
