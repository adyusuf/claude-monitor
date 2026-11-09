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

    /// <summary>
    /// The workspace the API's settings answer names. A move on the web keeps agent.json as it was, so the policies are judged against this one
    /// (falling back to agent.json's before any settings pass); their signatures bind the workspace id anyway.
    /// </summary>
    public const string WorkspaceKey = "settings.access_workspace";

    private const string Endpoint = "api/agent/access-policies";

    /// <summary>What a settings pass stores of the access fields, in the same transaction as its other keys.</summary>
    public static IEnumerable<(string Key, string Value)> Entries(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [(StampKey, settings.AccessPolicyStamp ?? ""), (EnforceKey, settings.EnforceAccessPolicies ? "true" : "false"),
            (WorkspaceKey, settings.WorkspaceId.ToString("D"))];
    }

    /// <summary>
    /// The workspace this agent's policies belong to. Where signed approvals are required it is agent.json's only, which only a local login
    /// changes: the API is not trusted there, so a workspace it reports can never set the signed policies aside (a move on the web then gives the
    /// default everywhere until the machine's admin logs in again). Elsewhere it is the one the API's settings name, else agent.json's; null when
    /// neither is known.
    /// </summary>
    public static Guid? CurrentWorkspace(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        var local = Identity.Peek(config)?.WorkspaceId;
        if (ApproversStore.Load(config).Required) return local;
        return Guid.TryParse(WorkspaceSettings.Get(config, store, WorkspaceKey), out var fromApi) ? fromApi : local;
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
    /// where signatures are required its kept policy stays until a newer signed message of the same project (a signed tombstone included) replaces it. A delivery
    /// at the cap is taken as cut: nothing held is dropped and every run gets the default until a full one arrives.
    /// </summary>
    private void Store(Guid marker, AgentAccessPolicies answer)
    {
        // The delivery's workspace must be the one the settings name: a mixed or foreign one cannot be placed, so every run gets the default and the
        // next pass asks again (the stamp is not recorded).
        var delivered = answer.Policies.Where(m => m is not null).ToList();
        var workspaces = delivered.Select(m => m.WorkspaceId).Distinct().ToList();
        if (CurrentWorkspace(config, store) is not { } workspace || workspaces.Count > 1 || (workspaces.Count == 1 && workspaces[0] != workspace))
        {
            store.Set(LocalStore.AccessTruncatedKey, "true");
            return;
        }

        var approvers = ApproversStore.Load(config);
        var ours = workspace.ToString("D");
        var held = store.AccessPolicies().Where(p => p.WorkspaceId == ours).ToDictionary(p => p.ProjectId, StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var truncated = answer.Policies.Count >= AgentLimits.Policies;
        delivered = [.. delivered.Take(AgentLimits.Policies)];
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

        foreach (var (id, before) in held.Where(h => !rows.ContainsKey(h.Key)))
        {
            if (truncated)
            {
                rows[id] = before;
                continue;
            }

            // Versions count per project: another project's signed message, however new, never releases this one (a replayed old message could).
            if (approvers.Required && AccessPolicyVerifier.Parse(before.KeptMessage) is not null) rows[id] = before with { Version = null, Message = null, IsSigned = false };
        }

        // Rows of another workspace (before a move on the web) are not passed on, so they go; where signatures are required a kept signed one stays
        // (it is not judged while the workspace differs), so only a local login (ForgetAccessPolicies) ever removes a signed policy.
        if (approvers.Required)
        {
            foreach (var other in store.AccessPolicies().Where(p => p.WorkspaceId != ours && p.KeptMessage is not null && !rows.ContainsKey(p.ProjectId)))
            {
                rows[other.ProjectId] = other;
            }
        }

        store.ReplaceAccessPolicies([.. rows.Values], Marker(marker, answer.Stamp ?? ""), truncated); // no stamp and an empty one: no policy
    }

    /// <summary>The stamp held, tagged with its workspace: a login into another workspace never reads the old set as current.</summary>
    private static string Marker(Guid workspace, string stamp) => workspace.ToString("D") + ":" + stamp;

    private static class AgentLimits
    {
        /// <summary>The most policies the API delivers to one agent; more is not taken.</summary>
        public const int Policies = 200;
    }
}
