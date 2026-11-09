using Podium.Agent.Access;
using Podium.Contracts;

namespace Podium.Agent.Exec;

// The access part of ExecGuard (ADR-0020 §4): after its own checks, the run as it would start (its real working directory, the real paths its grant's
// placeholders filled, its real executable, shell or script) is held to every access layer; a refusal names the layer and the policy version.
public static partial class ExecGuard
{
    /// <summary>The full check with the access layers this run is held to, as <paramref name="access"/> resolves them for it now.</summary>
    public static ExecDecision Check(RunMessage run, ExecPolicy policy, string os, string agentHome, AccessLayers access)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(access);
        return Check(run, policy, os, agentHome, access: access.For(run, policy));
    }

    private static ExecDecision WithAccess(ExecDecision decision, RunMessage run, IReadOnlyList<AccessLayer> layers, string os)
    {
        var check = new AccessCheck(decision.ResolvedCwd, decision.ResolvedPaths ?? [], decision.ResolvedExe, null, run.Mode == RunModes.Shell,
            run.Mode == RunModes.Script);
        return AccessGuard.Check(layers, check, os) is { } refusal ? ExecDecision.Refuse(refusal) : decision;
    }
}
