using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Config;

// Remote work (ADR-0005): service mode, the exec policy and the limits of runs, metrics and relays. Part of the ONE
// configuration module; FromEnvironment in AgentConfig.cs still reads every variable.
public sealed partial record AgentConfig
{
    /// <summary>
    /// True when the daemon runs as a boot service under its own account (ADR-0005); the service units set CM_SERVICE=1.
    /// Its exec level is then read from <see cref="ExecConfigPath"/>, an admin-owned file, never from agent.json.
    /// </summary>
    public bool ServiceMode { get; init; }

    /// <summary>The admin-owned exec policy of a service agent: level and optional ceiling (ADR-0005, "Four fail-closed keys").</summary>
    public string ExecConfigPath { get; init; } = DefaultExecConfigPath(Os);

    /// <summary>Remote runs executing at once on this target; one more is refused as busy.</summary>
    public int ExecMaxConcurrent { get; init; } = 2;
    public TimeSpan ExecTimeoutDefault { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ExecTimeoutMax { get; init; } = TimeSpan.FromSeconds(3600);

    /// <summary>After SIGTERM (or a Job Object close request) a run's processes get this long before they are killed.</summary>
    public TimeSpan ExecKillGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Output kept from the start of a run; past it only a tail of <see cref="RunTailBytes"/> is kept.</summary>
    public int RunHeadBytes { get; init; } = 256 * 1024;
    public int RunTailBytes { get; init; } = 256 * 1024;

    /// <summary>A run that prints more than this in total is killed (output_limit).</summary>
    public long RunReadCap { get; init; } = 64L * 1024 * 1024;

    /// <summary>The most one output chunk carries to the API (the API refuses more than 64 KB).</summary>
    public int RunChunkBytes { get; init; } = 32 * 1024;

    public TimeSpan MetricsEvery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Finished runs (and the output read from them) are deleted from the local database after this long.</summary>
    public TimeSpan RemoteLocalRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How often the daemon sends queued remote requests and polls open runs it asked for (fallback to run_update).</summary>
    public TimeSpan RemotePollEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the MCP process looks in the local database for an answer while a tool waits.</summary>
    public TimeSpan RemoteWaitPoll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest an MCP tool waits for a run before answering with its current status.</summary>
    public const int RemoteWaitMaxSeconds = 50;

    /// <summary>Linux /etc/cm-agent; macOS /Library/Application Support/ClaudeMonitor; Windows %ProgramFiles%\ClaudeMonitor — all admin-owned.</summary>
    public static string DefaultExecConfigPath(string os) => os switch
    {
        OsKinds.Windows => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeMonitor", "exec.json"),
        OsKinds.MacOs => "/Library/Application Support/ClaudeMonitor/exec.json",
        _ => "/etc/cm-agent/exec.json",
    };
}
