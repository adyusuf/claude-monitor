using System.Text.Json;
using Podium.Agent.Approvals;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// One stored policy as the machine judges it now: its version, one of <see cref="AccessPolicyStates"/>, whether it is signed and by which key, the
/// document that applies (null when the default applies instead: not valid, not signed where signatures are required, or cleared) and the repo keys
/// it covers, which decide the runs it holds to.
/// </summary>
public sealed record AppliedAccessPolicy(Guid ProjectId, int Version, string State, bool IsSigned, string? KeyId, AccessPolicyDocument? Document,
    IReadOnlyList<string> Keys);

/// <summary>
/// Which stored policy applies (ADR-0020 §4 rule 3), judged again on every read so that removing a pinned key takes effect at once. Where the machine
/// requires signed approvals (<see cref="ApproversStore"/>) a policy applies only when a pinned key signed it, for this agent's workspace, with a version
/// above the last kept one (no rollback); an unsigned or unknown-key change leaves the last signed policy in place. Elsewhere what was delivered applies
/// when it is valid. Invalid means the default for its repositories, never "no limit".
/// </summary>
public static class AccessPolicyVerifier
{
    /// <summary>Every stored policy judged with the pinned keys and the workspace agent.json names now.</summary>
    public static List<AppliedAccessPolicy> Load(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        return Judge(store.AccessPolicies(), ApproversStore.Load(config), Identity.Peek(config)?.WorkspaceId);
    }

    public static List<AppliedAccessPolicy> Judge(IReadOnlyList<StoredAccessPolicy> rows, ApproversPolicy approvers, Guid? workspace)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(approvers);
        return [.. rows.Select(row => Judge(row, approvers, workspace)).OfType<AppliedAccessPolicy>()];
    }

    private static AppliedAccessPolicy? Judge(StoredAccessPolicy row, ApproversPolicy approvers, Guid? workspace)
    {
        var delivered = Parse(row.Message);
        if (!approvers.Required)
        {
            if (row.Message is null) return null; // no longer delivered: nothing holds this machine to it
            if (delivered is null) return Unreadable(row);
            return Valid(delivered, workspace) is { } error ? Refused(delivered, error) : Applied(delivered, AccessPolicyStates.Applied);
        }

        var kept = Parse(row.KeptMessage);
        var deliveredError = delivered is null ? AccessPolicyStates.Invalid : Signed(delivered, approvers, workspace);
        var keptError = kept is null ? AccessPolicyStates.KeyUnknown : Signed(kept, approvers, workspace);
        if (delivered is not null && deliveredError is null && (kept is null || IsNewer(delivered, kept)))
        {
            return Applied(delivered, AccessPolicyStates.Applied);
        }

        if (kept is not null && keptError is null)
        {
            return Applied(kept, delivered is null || SameVersion(delivered, kept) ? AccessPolicyStates.Applied : AccessPolicyStates.KeptSigned);
        }

        // Nothing signed applies: the default does. A delivered version that verified but is not above the kept one is a rollback.
        if (delivered is not null) return Refused(delivered, deliveredError ?? AccessPolicyStates.RollbackRefused);
        if (kept is not null) return Refused(kept, keptError!);
        return row.Message is null && row.KeptMessage is null ? null : Unreadable(row);
    }

    /// <summary>
    /// Whether a delivered message replaces the kept one: it verifies with a pinned key and is newer (or the same version with the same hash). The sync
    /// keeps such a message, so an older signed version can never come back.
    /// </summary>
    public static bool Keeps(AccessPolicyMessage delivered, AccessPolicyMessage? kept, ApproversPolicy approvers, Guid? workspace)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        ArgumentNullException.ThrowIfNull(approvers);
        return Signed(delivered, approvers, workspace) is null && (kept is null || IsNewer(delivered, kept));
    }

    /// <summary>The message as stored, or null when it is not one.</summary>
    public static AccessPolicyMessage? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<AccessPolicyMessage>(json, ApiClient.Json);
        }
        catch (JsonException)
        {
            return null; // judged as invalid by the caller: the default applies
        }
    }

    private static bool IsNewer(AccessPolicyMessage candidate, AccessPolicyMessage kept) =>
        candidate.Version > kept.Version || (SameVersion(candidate, kept) && string.Equals(candidate.Hash, kept.Hash, StringComparison.Ordinal));

    private static bool SameVersion(AccessPolicyMessage a, AccessPolicyMessage b) => a.Version == b.Version;

    /// <summary>Null when the message is a valid policy of this workspace whose hash is its document's, else <see cref="AccessPolicyStates.Invalid"/>.</summary>
    private static string? Valid(AccessPolicyMessage message, Guid? workspace)
    {
        if (workspace is null || message.WorkspaceId != workspace || message.Version < 1 || AccessPolicyRules.Validate(message.Document) is not null)
        {
            return AccessPolicyStates.Invalid;
        }

        return string.Equals(AccessPolicySigning.Hash(message.Document), message.Hash, StringComparison.Ordinal) ? null : AccessPolicyStates.Invalid;
    }

    /// <summary>Null when the message is valid and a pinned key signed this version of it, else why not.</summary>
    private static string? Signed(AccessPolicyMessage message, ApproversPolicy approvers, Guid? workspace)
    {
        if (Valid(message, workspace) is { } invalid) return invalid;
        if (message.Signature is null || message.KeyId is null) return AccessPolicyStates.UnsignedIgnored;
        var pinned = approvers.Keys.FirstOrDefault(k => string.Equals(k.Id, message.KeyId, StringComparison.OrdinalIgnoreCase));
        if (pinned is null) return AccessPolicyStates.KeyUnknown;
        var key = ApprovalSigning.FromBase64(pinned.PublicKey);
        var signature = ApprovalSigning.FromBase64(message.Signature);
        return key is not null && signature is not null && AccessPolicySigning.Verify(key, message.WorkspaceId, message.ProjectId, message.Version, message.Hash, signature)
            ? null
            : AccessPolicyStates.Invalid;
    }

    private static AppliedAccessPolicy Applied(AccessPolicyMessage message, string state) =>
        message.Document.Cleared
            ? new(message.ProjectId, message.Version, AccessPolicyStates.Default, message.Signature is not null, message.KeyId, null, Keys(message))
            : new(message.ProjectId, message.Version, state, message.Signature is not null, message.KeyId, message.Document, Keys(message));

    private static AppliedAccessPolicy Refused(AccessPolicyMessage message, string state) =>
        new(message.ProjectId, message.Version, state, message.Signature is not null, message.KeyId, null, Keys(message));

    // A row whose message cannot be read: it is reported, and covers no repository it could name.
    private static AppliedAccessPolicy? Unreadable(StoredAccessPolicy row) =>
        Guid.TryParse(row.ProjectId, out var id) ? new(id, row.Version ?? row.KeptVersion ?? 0, AccessPolicyStates.Invalid, row.IsSigned, null, null, []) : null;

    private static List<string> Keys(AccessPolicyMessage message) => [.. (message.Document?.ProjectKeys ?? []).OfType<string>()];
}
