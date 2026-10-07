using System.Globalization;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// One pass of the daemon's chores, each callable on its own (and tested so): upload the outbox, hand permission
/// requests to the API, report commands the hooks applied, keep the workspace's settings, and take what arrives on
/// the agent's stream.
/// </summary>
public sealed class Relay(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock)
{
    /// <summary>Executes runs sent to this agent (ADR-0005); null where the relay only uploads.</summary>
    public RunRelay? Runs { get; init; }

    /// <summary>When the API last answered a heartbeat (ISO-8601 UTC): the hooks wait for the web only if it is recent.</summary>
    public const string LastContactKey = "relay.last_contact";

    /// <summary>The exception type name of the last failed heartbeat, empty after an answered one (an update judges a new daemon by it).</summary>
    public const string HeartbeatErrorKey = "relay.heartbeat_error";

    /// <summary>Tells the API this agent is alive; only an answered heartbeat counts as contact.</summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        await api.HeartbeatAsync(ct);
        store.Set(LastContactKey, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        store.Set(HeartbeatErrorKey, "");
    }

    public void HeartbeatFailed(string errorType) => store.Set(HeartbeatErrorKey, errorType);

    /// <summary>Sends batches until the outbox is empty. Returns the number of events the API acknowledged.</summary>
    public async Task<int> UploadAsync(CancellationToken ct)
    {
        var sent = 0;
        while (store.NextBatch(config.BatchEvents, config.BatchBytes) is { } batch)
        {
            var ack = await api.SendBatchAsync(new EventBatch(batch.Seq, batch.Rows.Select(r => r.Event).ToList()), ct);
            store.Acknowledge(batch.Seq);
            sent += ack.Duplicate ? 0 : batch.Rows.Count;
            UploadOutcome(null);
        }

        return sent;
    }

    /// <summary>New permission requests go to the API; ones past their wait are given up (Claude asks locally).</summary>
    public async Task PermissionsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var p in store.PermissionsIn(PermissionStates.New))
        {
            var left = p.WaitSeconds - (int)(now - p.CreatedAt).TotalSeconds;
            if (left <= 1)
            {
                store.PermissionExpired(p.LocalId);
                continue;
            }

            await UploadAsync(ct); // the session must exist on the API before a request can name it
            using var input = JsonDocument.Parse(p.ToolInput);
            var created = await api.CreatePermissionAsync(
                new PermissionRequestCreate(p.Harness, p.Session, p.ToolName, input.RootElement.Clone(), left), ct);
            if (created is not null) store.PermissionSent(p.LocalId, created.Id.ToString());
        }
    }

    /// <summary>A command a hook took has been applied to its session: the web hears so.</summary>
    public async Task ReportCommandsAsync(CancellationToken ct)
    {
        foreach (var taken in store.TakenCommands())
        {
            await api.CommandStatusAsync(taken.Id, CommandStatuses.Applied, null, ct, taken.At);
            store.MarkCommandReported(taken.Id);
        }
    }

    public async Task SettingsAsync(CancellationToken ct)
    {
        // The tag is the workspace agent.json names as the pass begins, not the one in the answer: a pass that `cm-agent login`
        // overtakes is then tagged with the old workspace and reads as off, while an agent moved to another workspace on the web
        // (same tokens, agent.json unchanged) keeps trusting what the server now answers for it.
        // Stamped with the time the read BEGAN: the server answers with the switch as of some moment during the request, so a
        // stamp taken when the answer arrives could look newer than a decision the answer does not reflect. The tag and the
        // stamp are taken together under the gate StoreRead uses: a pass pre-empted between the two could otherwise carry an
        // older workspace than a pass that began before it, and its later stamp would then make StoreRead drop the newer one.
        Guid? workspace;
        DateTimeOffset began;
        lock (settingsGate)
        {
            workspace = Identity.Peek(config)?.WorkspaceId;
            began = clock.GetUtcNow();
        }

        var s = await api.SettingsAsync(ct);
        // One transaction for the values AND their tag: two passes (this loop's and the stream's re-read) on separate connections
        // cannot leave one pass's values under the other's tag.
        List<(string Key, string Value)> entries =
        [
            ("settings.mask_secrets", s.MaskSecrets ? "true" : "false"),
            ("settings.event_max_bytes", s.EventMaxBytes.ToString(CultureInfo.InvariantCulture)),
            (UpdatePolicy.WorkspaceKey, UpdateModes.Normalize(s.AgentUpdate)),
            .. MachineMonitor.Entries(s),
            (ClaudeUpdate.ClaudePolicy.WorkspaceKey, s.ClaudeUpdate ? "true" : "false"),
        ];
        if (workspace is { } w) entries.Add(WorkspaceSettings.TagEntry(w));
        StoreRead(began, entries);
    }

    // UTC ticks of the newest successful read's start. Two passes (this loop's and the stream's re-read) may overlap, so the
    // values and this stamp are written together, under one lock, and only by the pass that began later (see StoreRead).
    private long lastSettingsTicks = DateTimeOffset.MinValue.UtcTicks;

    private readonly Lock settingsGate = new();

    // Readable without the lock: the ticks are only ever replaced whole.
    private DateTimeOffset LastSettingsAt => new(Interlocked.Read(ref lastSettingsTicks), TimeSpan.Zero);

    // The values and the stamp of one pass go in as one step. A pass that began earlier than the stamp already stored lost the
    // race to a newer pass whose (fresher) values are in the store, so its older answer is dropped instead of overwriting them.
    // A stamp after the current time cannot be right (the clock was stepped back since): it is not compared against, so the
    // pass that now reads replaces it, even with an earlier time.
    private void StoreRead(DateTimeOffset began, List<(string Key, string Value)> entries)
    {
        lock (settingsGate)
        {
            var stored = LastSettingsAt;
            if (stored <= clock.GetUtcNow() && began < stored) return;
            store.SetMany(entries);
            Interlocked.Exchange(ref lastSettingsTicks, began.UtcTicks);
        }
    }

    /// <summary>
    /// A settings read whose request began this recently is reused when a run finds the switch off: a burst of runs costs one read.
    /// The age is measured from the time the read began, not from when its answer arrived.
    /// </summary>
    public static readonly TimeSpan SettingsReuse = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How far a run's decision time (the server's clock) may be from the agent's clock for a cached settings read to still count as
    /// begun after it: the read is trusted only when it began at least this much later. A server clock ahead of the agent's just costs
    /// an extra read (it fails toward reading); one behind it by more than this could leave a read taken just before the decision
    /// looking later, which is why the margin is there. A few seconds is far beyond what synchronised hosts differ by.
    /// </summary>
    public static readonly TimeSpan DecisionSkew = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The workspace's remote-runs switch for this run. Unread, another workspace's or off is no; but a switch an admin has
    /// just turned on is only learned at the next settings pass (SettingsEvery), so before refusing, the settings are read
    /// again once. A read under <see cref="SettingsReuse"/> old is reused only if it was taken after the run was decided: the
    /// server checks the switch when it approves, so a run approved after the read may have a switch the read does not show.
    /// A stored read tagged with a workspace other than the one agent.json now names (a login moved the machine) is not a recent
    /// read of this workspace: it forces the read whatever its age, once per run. A failed read is not swallowed: the stream
    /// reconnects and the server replays the approved run.
    /// </summary>
    private async Task<bool> RemoteRunsOnAsync(RunMessage run, CancellationToken ct)
    {
        if (WorkspaceSettings.Get(config, store, MachineMonitor.RemoteRunsKey) == "true") return true;
        var now = clock.GetUtcNow();
        var readBegan = LastSettingsAt;
        if (readBegan > now) readBegan = DateTimeOffset.MinValue; // a stamp from the future cannot be trusted: read again
        var decidedAfterRead = run.DecidedAt is { } decided && readBegan < decided + DecisionSkew;
        if (now - readBegan >= SettingsReuse || decidedAfterRead || WorkspaceSettings.TagIsStale(config, store)) await SettingsAsync(ct);
        return WorkspaceSettings.Get(config, store, MachineMonitor.RemoteRunsKey) == "true";
    }

    /// <summary>Records the state of the stream for `monitor_status` and `cm-agent status` (ids, times and error type names only).</summary>
    public void StreamState(string state, int failures, string? error)
    {
        store.Set(PushStatus.StreamStateKey, state);
        store.Set(PushStatus.StreamStateAtKey, Now());
        store.Set(PushStatus.StreamFailuresKey, failures.ToString(CultureInfo.InvariantCulture));
        store.Set(PushStatus.StreamErrorKey, error ?? "");
    }

    /// <summary>Records the outcome of an upload pass: its time, or the failure's type name.</summary>
    public void UploadOutcome(string? error)
    {
        if (error is null)
        {
            store.Set(PushStatus.UploadAtKey, Now());
            store.Set(PushStatus.UploadErrorKey, "");
            return;
        }

        store.Set(PushStatus.UploadErrorKey, error);
        store.Set(PushStatus.UploadErrorAtKey, Now());
    }

    private string Now() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Takes one stream message. Returns false when the agent was revoked and must stop.</summary>
    public async Task<bool> OnStreamAsync(string name, JsonElement data, CancellationToken ct)
    {
        switch (name)
        {
            case AgentStreamEvents.Command:
                var c = data.Deserialize<AgentCommandMessage>(ApiClient.Json)!;
                store.Set(PushStatus.StreamCommandKey, c.Id.ToString());
                store.Set(PushStatus.StreamCommandAtKey, Now());
                store.SaveCommand(new LocalCommand(c.Id.ToString(), c.SessionExternalId, c.Kind, c.Body, c.ExpiresAt));
                await api.CommandStatusAsync(c.Id.ToString(), CommandStatuses.Delivered, null, ct);
                return true;
            case AgentStreamEvents.PermissionAnswer:
                var a = data.Deserialize<PermissionAnswerMessage>(ApiClient.Json)!;
                store.PermissionAnswered(a.Id.ToString(), a.Decision, a.Reason, a.Answers is null ? null : JsonSerializer.Serialize(a.Answers));
                return true;
            case AgentStreamEvents.Run:
                var run = data.Deserialize<RunMessage>(ApiClient.Json)!;
                if (Runs is null || !await RemoteRunsOnAsync(run, ct))
                {
                    if (store.ExecBegin(run.Id.ToString(), clock.GetUtcNow()))
                    {
                        store.ExecEnd(run.Id.ToString(), RunStatuses.Failed, null, RemoteErrors.Disabled, false, null);
                    }

                    return true;
                }

                Runs.Accept(run, ct);
                return true;
            case AgentStreamEvents.RunCancel:
                Runs?.Cancel(data.Deserialize<RunCancelMessage>(ApiClient.Json)!.Id);
                return true;
            case AgentStreamEvents.RunUpdate:
                store.RunChanged(data.Deserialize<RunUpdateMessage>(ApiClient.Json)!.Id.ToString());
                return true;
            case AgentStreamEvents.Revoked:
                return false;
            default:
                return true;
        }
    }
}
