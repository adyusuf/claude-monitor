namespace ClaudeMonitor.Contracts;

/// <summary>
/// Which per-call approvals need the approver's two-step code (ADR-0008 §3c, decision §9.13): on a strict target (production or database) every
/// way a person can make the machine run arbitrary text: a script, a shell command, and an argv run of an interpreter. Other argv runs and
/// runs a grant or job covers keep the older rules; grants and jobs on a strict target need the code when they are approved (see the API).
/// One definition for the API (approval, delivery, the web view) and the target agent.
/// </summary>
public static class RunCodeRule
{
    public static bool NeedsCode(string? mode, IReadOnlyList<string>? argv, string? os, bool strict) =>
        strict && mode switch
        {
            RunModes.Script or RunModes.Shell => true,
            RunModes.Argv => argv is { Count: > 0 } && os is not null && GrantMatcher.IsInterpreter(argv[0], os),
            _ => false,
        };
}
