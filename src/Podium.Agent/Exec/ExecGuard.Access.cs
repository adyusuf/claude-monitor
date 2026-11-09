using Podium.Agent.Access;
using Podium.Contracts;

namespace Podium.Agent.Exec;

// The access part of ExecGuard (ADR-0020 §4): after its own checks, the run as it would start (its real working directory, the real paths its grant's
// placeholders filled, its real executable, shell or script) is held to every access layer; a refusal names the layer and the policy version.
public static partial class ExecGuard
{
    /// <summary>
    /// The full check with the access layers this run is held to, as <paramref name="access"/> resolves them now for the repositories its real
    /// working directory and paths lie in as well as the one it names (a label alone never picks the policy).
    /// </summary>
    public static ExecDecision Check(RunMessage run, ExecPolicy policy, string os, string agentHome, AccessLayers access)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(access);
        var decision = CheckRun(run, policy, os, agentHome, null, null);
        if (!decision.Allowed) return decision;
        var (layers, refusal) = access.For(run, policy, decision.ResolvedCwd, decision.ResolvedPaths ?? []);
        return refusal is not null ? ExecDecision.Refuse(refusal) : WithAccess(decision, run, layers, os);
    }

    private static ExecDecision WithAccess(ExecDecision decision, RunMessage run, IReadOnlyList<AccessLayer> layers, string os)
    {
        var check = new AccessCheck(decision.ResolvedCwd, decision.ResolvedPaths ?? [], decision.ResolvedExe, null, run.Mode == RunModes.Shell,
            run.Mode == RunModes.Script);
        // An argv run of an interpreter can run any code it is given: it counts as a script for every layer but the ceiling.
        var interpreter = run.Mode == RunModes.Argv && decision.ResolvedExe is { } exe && GrantMatcher.IsInterpreter(exe, os);
        return AccessGuard.Check(layers, check, os, interpreter) is { } refusal ? ExecDecision.Refuse(refusal) : decision;
    }
}
