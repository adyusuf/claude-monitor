using System.Net;
using System.Text.Json;
using Podium.Agent.Approvals;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// Keeps the access policies of this agent's repositories (ADR-0020 §4). The settings carry only a stamp; when it differs from the stamp of the set
/// last fetched, the set is fetched again (<c>GET api/agent/access-policies</c>) and replaces what was delivered before, while the last policy signed by a
/// pinned key is kept beside it. An API that does not know the call (404) leaves the store as it is. A run that names a newer stamp than the one held
/// makes the agent fetch once before it runs; a failed fetch is not swallowed, so the server replays the run.
/// </summary>
public sealed class AccessPolicySync(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock)
{
    /// <summary>The stamp of the policy set the settings named last (empty: none), under the workspace tag like every workspace switch.</summary>
    public const string StampKey = "settings.access_stamp";

    /// <summary>The workspace's switch: "true" when a run whose repository has no policy gets the default.</summary>
    public const string EnforceKey = "settings.enforce_access";

    private const string Endpoint = "api/agent/access-policies";

    /// <summary>What a settings pass stores of the access fields, in the same transaction as its other keys.</summary>
    public static IEnumerable<(string Key, string Value)> Entries(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [(StampKey, settings.AccessPolicyStamp ?? ""), (EnforceKey, settings.EnforceAccessPolicies ? "true" : "false")];
    }

    /// <summary>Fetches the set when the settings name another stamp than the one held (the settings loop).</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (Identity.Peek(config)?.WorkspaceId is not { } workspace) return;
        var wanted = Marker(workspace, WorkspaceSettings.Get(config, store, StampKey) ?? "");
        if (wanted == store.Get(LocalStore.AccessSyncedKey)) return;
        await FetchAsync(workspace, wanted, remember: true, ct);
    }

    /// <summary>
    /// True when the run may go on: it names no policy, the stamp held, or one the agent caught up with now. False when the API could not deliver the set
    /// the run names (404: an older API, or the agent's user is no longer a member): the run is then refused as <see cref="AccessErrors.Stale"/> and its
    /// refusal stored for the report. The stamp also changes with the workspace's enforcement switch, so the settings are read again first
    /// (<paramref name="readSettings"/>); a failed read or fetch propagates and the server replays the run.
    /// </summary>
    public async Task<bool> FreshAsync(RunMessage run, Func<CancellationToken, Task> readSettings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(readSettings);
        if (Identity.Peek(config)?.WorkspaceId is not { } workspace) return true;
        // No stamp (an older API) and an empty one are the same: no policy for this machine. A set still held is fetched again once, and an API
        // without the call (404) leaves it in place, which only keeps the run stricter.
        var stamp = run.AccessStamp ?? "";
        var wanted = Marker(workspace, stamp);
        if (wanted == store.Get(LocalStore.AccessSyncedKey)) return true;
        if (stamp.Length > 0) await readSettings(ct); // an empty stamp is "no policy, switch off": nothing in the settings to catch up with
        var fetched = await FetchAsync(workspace, wanted, remember: stamp.Length == 0, ct);
        if (fetched || stamp.Length == 0) return true;
        if (store.ExecBegin(run.Id.ToString(), clock.GetUtcNow()))
        {
            store.ExecEnd(run.Id.ToString(), RunStatuses.Failed, null, AccessErrors.Stale, false, null, AccessRefusedBy.Policy, null);
        }

        return false;
    }

    /// <summary>Fetches and stores the set; false when the API does not know the call (the store is left as it is, and the stamp remembered when asked).</summary>
    private async Task<bool> FetchAsync(Guid workspace, string wanted, bool remember, CancellationToken ct)
    {
        JsonElement json;
        try
        {
            json = await api.RemoteAsync(HttpMethod.Get, Endpoint, null, ct);
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            if (remember) store.Set(LocalStore.AccessSyncedKey, wanted); // an older API: asked once per stamp, the policies already held stay
            return false;
        }

        var answer = json.ValueKind == JsonValueKind.Object ? json.Deserialize<AgentAccessPolicies>(ApiClient.Json) : null;
        if (answer?.Policies is null) throw new InvalidDataException("the access policy answer is malformed");
        Store(workspace, answer);
        return true;
    }

    /// <summary>
    /// The API delivers every live policy and tombstone of the workspace. Rows of another workspace go. A delivered message replaces the row's delivered
    /// one, and its kept one when it verifies and is newer. A row the full delivery no longer lists goes on a machine that does not require signatures;
    /// where signatures are required its kept policy stays only while no delivered signed policy of a higher version covers one of its keys. A delivery
    /// at the cap is taken as cut: nothing held is dropped and every run gets the default until a full one arrives.
    /// </summary>
    private void Store(Guid workspace, AgentAccessPolicies answer)
    {
        var approvers = ApproversStore.Load(config);
        var ours = workspace.ToString("D");
        var held = store.AccessPolicies().Where(p => p.WorkspaceId == ours).ToDictionary(p => p.ProjectId, StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var truncated = answer.Policies.Count >= AgentLimits.Policies;
        var delivered = answer.Policies.Where(m => m is not null && m.WorkspaceId == workspace).Take(AgentLimits.Policies).ToList();
        var rows = new Dictionary<string, StoredAccessPolicy>(StringComparer.Ordinal);
        foreach (var message in delivered)
        {
            var id = message.ProjectId.ToString("D");
            held.TryGetValue(id, out var before);
            var kept = AccessPolicyVerifier.Parse(before?.KeptMessage);
            var json = JsonSerializer.Serialize(message, ApiClient.Json);
            var keeps = AccessPolicyVerifier.Keeps(message, kept, approvers, workspace);
            rows[id] = new StoredAccessPolicy(id, ours, message.Version, json, message.Signature is not null, now,
                keeps ? message.Version : before?.KeptVersion, keeps ? json : before?.KeptMessage);
        }

        var signedDelivered = delivered.Where(m => AccessPolicyVerifier.Keeps(m, null, approvers, workspace)).ToList();
        foreach (var (id, before) in held.Where(h => !rows.ContainsKey(h.Key)))
        {
            if (truncated)
            {
                rows[id] = before;
                continue;
            }

            if (!approvers.Required || AccessPolicyVerifier.Parse(before.KeptMessage) is not { } kept) continue;
            var replaced = signedDelivered.Any(m => m.Version > kept.Version
                && m.Document.ProjectKeys.Any(k => kept.Document?.ProjectKeys?.Contains(k, RepoKeys.Comparer) == true));
            if (!replaced) rows[id] = before with { Version = null, Message = null, IsSigned = false };
        }

        store.ReplaceAccessPolicies([.. rows.Values], Marker(workspace, answer.Stamp ?? ""), truncated); // no stamp and an empty one: no policy
    }

    /// <summary>The stamp held, tagged with its workspace: a login into another workspace never reads the old set as current.</summary>
    private static string Marker(Guid workspace, string stamp) => workspace.ToString("D") + ":" + stamp;

    private static class AgentLimits
    {
        /// <summary>The most policies the API delivers to one agent; more is not taken.</summary>
        public const int Policies = 200;
    }
}
