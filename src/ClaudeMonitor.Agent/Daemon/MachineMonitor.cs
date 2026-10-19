using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Approvals;
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
    Func<ExecPolicy>? policySource = null, Func<(ClaudePresence? Claude, IReadOnlyList<ToolInfo> Tools)>? factsSource = null,
    Func<IReadOnlyList<ServiceInfo>>? servicesSource = null, Func<IReadOnlyList<ProjectFact>>? projectsSource = null,
    ClaudeInventoryReader? inventoryReader = null)
{
    public const string ThresholdsKey = "settings.alert_thresholds";
    public const string RemoteRunsKey = "settings.remote_runs";
    public const string ShareServicesKey = "settings.share_services";
    public const string ClaudeAutoKey = "settings.claude_auto";
    public const int KeepSamples = 60;
    /// <summary>A folder a session worked in is read for its commit and tag for this long after the last visit (ADR-0008 §3a′).</summary>
    public static readonly TimeSpan VisitedFor = TimeSpan.FromDays(30);
    private static readonly AlertThresholds Defaults = new(90, 90, 90, 300);
    private readonly AlertEvaluator evaluator = new(Defaults);
    private readonly List<MetricSample> samples = [];
    private readonly List<AlertReport> alerts = [];
    private bool unsupported;
    private string? lastSent;
    private (ClaudePresence? Claude, IReadOnlyList<ToolInfo> Tools)? facts;
    private DateTimeOffset factsAt;
    private string factsStamp = "";
    private (IReadOnlyList<ServiceInfo>? Services, IReadOnlyList<ProjectFact>? Projects, bool Share)? extras;
    private DateTimeOffset extrasAt;
    private ClaudeInventory? inventory;
    private DateTimeOffset inventoryAt;

    /// <summary>The exec level this agent allows now: the admin-owned file in service mode, agent.json otherwise (with the role claim, ADR-0008).</summary>
    public ExecPolicy Policy()
    {
        if (policySource is not null) return policySource();
        var saved = Identity.Peek(config);
        return ExecPolicyLoader.Load(config, saved?.ExecLevel, saved?.ClaimedEnvironment, saved?.ClaimedFunction, saved?.ProjectFolders);
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
        var (services, projects) = Extras(policy);
        var inventory = await InventoryAsync(policy, ct);
        var profile = new AgentProfile(level, config.ServiceMode, RuntimeInformation.OSDescription, Environment.UserName, config.ExecMaxConcurrent,
            policy.ClaimedEnvironment, policy.ClaimedFunction, claude, ToolInventory.Interpreters(tools), tools, Capabilities(level, tools, claude), services, projects, PushReported(), ApproversStore.Load(config).Required, inventory);
        var json = System.Text.Json.JsonSerializer.Serialize(profile with { Claude = claude is null ? null : claude with { SeenAt = default }, Inventory = inventory is null ? null : inventory with { At = default } }, ApiClient.Json);
        if (json == lastSent) return;
        if (await Guard(() => api.ProfileAsync(profile, ct))) lastSent = json;
    }

    /// <summary>What Claude Code has here (sessions, plugins, skills, project settings), read again at most every <see cref="AgentConfig.ClaudeInventoryEvery"/>; null when no reader was given (tests) or it failed.</summary>
    private async Task<ClaudeInventory?> InventoryAsync(ExecPolicy policy, CancellationToken ct)
    {
        if (inventoryReader is null) return null;
        var now = clock.GetUtcNow();
        if (inventory is { } cached && now - inventoryAt < config.ClaudeInventoryEvery && now >= inventoryAt) return cached;
        try
        {
            var dirs = ClaudeUpdate.ClaudeSessions.ConfigDirs(config, store, now, TimeSpan.FromDays(1));
            var folders = (policy.ProjectFolders ?? []).Concat(store.VisitedFolders(now - VisitedFor)).Distinct(StringComparer.Ordinal).Take(ProjectFolderList.Max * 2).ToList();
            inventory = await inventoryReader.ReadAsync(dirs, folders, now, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            inventory = null; // not reported this time; the next pass tries again
        }

        inventoryAt = now;
        return inventory;
    }

    /// <summary>The push transport this machine has on now (the saved setting, read fresh), or null when push is off: the web words its idle-session warning by it.</summary>
    private string? PushReported()
    {
        var current = SavedSettings.Apply(config);
        return current.PushEnabled ? current.PushTransport : null;
    }

    /// <summary>
    /// The listening services (only while the owner opted in, which the settings pass remembered) and the facts of the listed project folders,
    /// read again every <see cref="AgentConfig.ProjectFactsEvery"/> or at once when the opt-in changes.
    /// </summary>
    /// <summary>What this agent says it can do beyond runs (ADR-0008 §3e): `script`, when the level is shell without a ceiling and a trusted interpreter exists, and `ask`.</summary>
    private List<string> Capabilities(string level, IReadOnlyList<ToolInfo> tools, ClaudePresence? claude)
    {
        var list = new List<string>();
        if (level == ExecLevels.Shell && ToolInventory.Interpreters(tools).Count > 0)
        {
            list.Add(AgentCapabilities.Script);
            list.Add(AgentCapabilities.ScriptFiles); // this agent writes a script's files and stdin (ADR-0010 §2)
        }
        // `ask`: the inbox push is on, this machine did not turn asks off, and Claude Code is here to receive one (ADR-0008 §3e).
        if (config.InboxPushEnabled && config.AsksEnabled && claude is { Kind: not ClaudeKinds.None }) list.Add(AgentCapabilities.Ask);
        return list;
    }

    private (IReadOnlyList<ServiceInfo>? Services, IReadOnlyList<ProjectFact>? Projects) Extras(ExecPolicy policy)
    {
        var now = clock.GetUtcNow();
        var share = store.Get(ShareServicesKey) == "true";
        if (extras is { } cached && cached.Share == share && now - extrasAt < config.ProjectFactsEvery && now >= extrasAt) return (cached.Services, cached.Projects);
        store.ForgetFolders(now - 2 * VisitedFor);
        var services = share ? servicesSource?.Invoke() ?? ListeningServices.Live() : null;
        var read = projectsSource?.Invoke() ?? new ProjectFacts(config).Read(policy.ProjectFolders, store.VisitedFolders(now - VisitedFor), ProjectTestCommands.Saved(config));
        extras = (services, read.Count > 0 ? read : null, share);
        extrasAt = now;
        return (extras.Value.Services, extras.Value.Projects);
    }

    private (ClaudePresence? Claude, IReadOnlyList<ToolInfo> Tools) Facts()
    {
        var now = clock.GetUtcNow();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stamp = factsSource is null ? ToolInventory.Stamp(ToolInventory.Candidates(AgentConfig.Os, profile), ClaudeUpdate.ClaudeLocator.Locate(config).Path) : "";
        if (facts is { } cached && stamp == factsStamp && now - factsAt < config.PresenceProbeEvery && now >= factsAt)
        {
            // the sessions change often and cost one folder read; the inventory and the version are kept for the probe interval
            var (live, idle) = PresenceProbe.Sessions(config, store, now);
            return (cached.Claude is { } c ? c with { SessionsLive = live, SessionsIdle = idle, SeenAt = now } : null, cached.Tools);
        }

        // The Claude version goes through the same rule as every tool: a binary the agent may not run is never started (ADR-0008 §3a′).
        var fresh = factsSource?.Invoke() ?? (PresenceProbe.Probe(config, store, real => ToolInventory.VersionIfRunnable(ToolNames.Claude, real, config.ToolVersionTimeout, AgentConfig.Os), now),
            ToolInventory.Live(config, AgentConfig.Os));
        facts = fresh;
        factsAt = now;
        factsStamp = stamp;
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
        List<(string Key, string Value)> entries = [(RemoteRunsKey, settings.RemoteRuns ? "true" : "false"), (ShareServicesKey, settings.ShareServices ? "true" : "false"),
            (ClaudeAutoKey, settings.ClaudeAuto == ClaudeAutoModes.OnMessage ? ClaudeAutoModes.OnMessage : ClaudeAutoModes.Off)];
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
