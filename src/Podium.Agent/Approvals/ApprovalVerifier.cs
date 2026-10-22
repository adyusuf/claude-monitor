using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Contracts;

namespace Podium.Agent.Approvals;

/// <summary>
/// The target's own check of a signed approval (ADR-0010 §4), before it runs anything. In <c>required</c> mode every run needs the signature of a pinned owner key over the
/// run as THIS agent sees it: its hash is recomputed from the message, so an API that changed the command after the owner signed produces a different hash and a refused
/// run. A run approved by a grant or a job carries no signature and is refused (the API does not auto-approve for such a target; this is the same rule from the other side).
/// </summary>
public static class ApprovalVerifier
{
    /// <summary>How long after the signed expiry a delivered run is still accepted: the start window of an approved run.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <summary>The refusal code for this run, or null when it may go on to the other checks. Reads the policy and the agent's id fresh, so a pin takes effect at once.</summary>
    public static string? Refusal(AgentConfig config, RunMessage run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policy = ApproversStore.Load(config);
        return Refusal(policy, Identity.Peek(config)?.AgentId, run, now);
    }

    public static string? Refusal(ApproversPolicy policy, Guid? agentId, RunMessage run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(run);
        if (!policy.Required) return null;
        if (policy.Keys.Count == 0 || agentId is null) return ApprovalErrors.KeyMissing;
        if (run.GrantId is not null || run.Grant is not null || run.ApprovalSignature is null || run.ApprovalKeyId is null || run.ApprovalNotAfter is null) return ApprovalErrors.Required;
        var pinned = policy.Keys.FirstOrDefault(k => string.Equals(k.Id, run.ApprovalKeyId, StringComparison.OrdinalIgnoreCase));
        if (pinned is null) return ApprovalErrors.KeyUnknown;
        var key = ApprovalSigning.FromBase64(pinned.PublicKey);
        var signature = ApprovalSigning.FromBase64(run.ApprovalSignature);
        if (key is null || signature is null) return ApprovalErrors.Invalid;
        if (now > run.ApprovalNotAfter.Value + Grace) return ApprovalErrors.Invalid;
        var hash = HashOf(run, agentId.Value);
        return hash is not null && ApprovalSigning.Verify(key, ApprovalSigning.Payload(ApprovalSigning.KindRun, run.Id, agentId.Value, hash, run.ApprovalNotAfter.Value), signature)
            ? null
            : ApprovalErrors.Invalid;
    }

    /// <summary>The hash of the run as the agent received it, the one the owner's card carried; null for a mode this build does not know.</summary>
    public static string? HashOf(RunMessage run, Guid agentId) => run.Mode switch
    {
        RunModes.Argv or RunModes.Shell => GrantMatcher.RunHash(run.Mode, run.Argv, run.ShellCommand, run.Cwd, run.TimeoutSeconds, agentId),
        RunModes.Script => GrantMatcher.ScriptHash(run.ScriptInterpreter ?? "", run.ScriptBody ?? "", run.ScriptArgs, run.TimeoutSeconds, agentId,
            run.TargetEnvironment, run.TargetFunction, run.ScriptFiles, run.ScriptStdin),
        _ => null,
    };
}
