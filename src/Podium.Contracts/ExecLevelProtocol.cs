using System.Text;

namespace Podium.Contracts;

/// <summary>
/// Turning a machine's remote-run level up or down from its page on the web (ADR-0021). Raising it (off -> argv -> shell) is accepted by the machine
/// only when one of the owner keys pinned on the machine (<c>aipod approvers add</c>, ADR-0010) signed exactly this change, so neither a compromised
/// server nor a stolen web session can turn remote runs on. Lowering needs no signature: it never widens what runs.
/// </summary>
public static class ExecLevelSigning
{
    public const string Tag = "ap-exec-level-v1";

    /// <summary>The longest a signed change stays valid after it is signed.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The bytes the owner's browser signs: tag, machine (agent) id, level, the change's sequence number and its deadline (Unix seconds).</summary>
    public static byte[] Payload(Guid agentId, string level, long sequence, DateTimeOffset notAfter) =>
        Encoding.UTF8.GetBytes($"{Tag}\n{agentId:D}\n{level}\n{sequence}\n{notAfter.ToUnixTimeSeconds()}");

    /// <summary>The rank of a level: off 0, argv 1, shell 2; -1 for an unknown one.</summary>
    public static int Rank(string? level) => level switch
    {
        ExecLevels.Off => 0,
        ExecLevels.Argv => 1,
        ExecLevels.Shell => 2,
        _ => -1,
    };

    /// <summary>True when going from one level to another widens what may run, so the change must be signed.</summary>
    public static bool Raises(string? from, string to) => Rank(to) > Math.Max(Rank(from), 0);
}

/// <summary>
/// The owner asks for a level (web -> API). Sequence must be the machine's current sequence + 1 (compare-and-set). KeyId and Signature are required
/// when the change raises the level; Code is the re-authentication (a fresh two-step code on a production or database machine).
/// </summary>
public sealed record ExecLevelChange(string Level, long Sequence, DateTimeOffset NotAfter, string? KeyId = null, string? Signature = null, string? Code = null);

/// <summary>The latest change the API holds for an agent, delivered in its settings. Signature is base64 (IEEE P1363), KeyId the key's fingerprint.</summary>
public sealed record ExecLevelRequest(string Level, long Sequence, DateTimeOffset NotAfter, string? KeyId = null, string? Signature = null);

/// <summary>What the agent did with the latest change, reported in its profile.</summary>
public sealed record ExecLevelReport(long Sequence, string State);

public static class ExecLevelStates
{
    /// <summary>The level is in place.</summary>
    public const string Applied = "applied";

    /// <summary>A raise without a valid signature of a pinned owner key.</summary>
    public const string SignatureRefused = "signature_refused";

    /// <summary>No owner key is pinned on the machine, so a raise cannot be checked: pin one with aipod approvers add.</summary>
    public const string NoPinnedKey = "no_pinned_key";

    /// <summary>The change arrived after its deadline.</summary>
    public const string Expired = "expired";

    /// <summary>A service's level lives in its admin-owned exec policy file, which the service cannot change: the admin sets it.</summary>
    public const string NeedsAdmin = "needs_admin";

    /// <summary>The level is not one the agent knows.</summary>
    public const string Invalid = "invalid";

    /// <summary>
    /// The change's sequence is below the last one this machine handled, or jumps implausibly far without a signature: it is not applied. Logging in
    /// again on the machine starts the count over.
    /// </summary>
    public const string Stale = "stale";

    public static readonly IReadOnlyList<string> All = [Applied, SignatureRefused, NoPinnedKey, Expired, NeedsAdmin, Invalid, Stale];
}

public static class ExecLevelErrors
{
    public const string Changed = "exec_level_changed";
    public const string Invalid = "exec_level_invalid";
    public const string SignatureRequired = "exec_level_signature_required";
}
