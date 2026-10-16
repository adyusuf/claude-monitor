namespace ClaudeMonitor.Agent.Config;

// Machine awareness (ADR-0008 §3a′, §3b, §3e): the session-start block, the machines cache and the presence probe. Part of
// the ONE configuration module; FromEnvironment reads every variable.
public sealed partial record AgentConfig
{
    /// <summary>The local SQLite keys the daemon renders the session-start block into (read by `hook SessionStart --context`).</summary>
    public const string MachinesContextKey = "machines.context";
    public const string MachinesContextAtKey = "machines.context_at";

    /// <summary>CM_MACHINES_CONTEXT=off turns the block off on this machine; default on (it is empty anyway while remote runs are off).</summary>
    public bool MachinesContextEnabled { get; init; } = true;

    /// <summary>The most characters of the machine list inside the block's envelope; the fixed rules outside it are not counted.</summary>
    public int MachinesContextMax { get; init; } = 2000;

    /// <summary>CM_MACHINE_CACHE_EVERY: how often the daemon re-reads the workspace's machines for the block (30 s to 1 h).</summary>
    public TimeSpan MachineCacheEvery { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>CM_PRESENCE_PROBE_EVERY: how often the tool inventory and the Claude version are probed again (1 min to 1 day); a changed binary is probed sooner.</summary>
    public TimeSpan PresenceProbeEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>CM_PROJECT_FACTS_EVERY: how often the listed project folders' .git facts and, when the owner opted in, the listening services are read again (30 s to 1 h).</summary>
    public TimeSpan ProjectFactsEvery { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a tool's --version may take before the probe gives up on it.</summary>
    public TimeSpan ToolVersionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The whole SessionStart --context hook may read the block for at most this long (it always exits 0).</summary>
    public TimeSpan ContextReadWait { get; init; } = TimeSpan.FromSeconds(1);
}
