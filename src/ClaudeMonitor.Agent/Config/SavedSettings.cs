using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// The settings a person can change after the fact without editing the environment of every hook process: how long the
/// Stop hook waits for a prompt from the web (`cm-agent install --stop-wait`) and whether prompts are pushed into an idle
/// session (`--push`). They are kept in agent.json beside the identity. CM_STOP_WAIT and CM_PUSH, when set, win (tests and
/// one-off overrides); the defaults stay 0 and off, except that the first `cm-agent install` saves the inbox push as on.
/// </summary>
public static class SavedSettings
{
    /// <summary>The configuration with the saved stop wait applied; unchanged when CM_STOP_WAIT is set or nothing is saved.</summary>
    public static AgentConfig Apply(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var saved = Identity.Peek(config);
        if (!config.StopWaitFromEnvironment && saved?.StopWaitSeconds is { } seconds)
        {
            config = config with { StopWait = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, AgentConfig.WaitMaxSeconds)) };
        }

        if (!config.ClaudeUpdateFromEnvironment && saved?.ClaudeUpdate is { } claude) config = config with { ClaudeUpdateEnabled = claude };
        if (!config.AutoUpdateFromEnvironment && saved?.AutoUpdate is { } update) config = config with { AutoUpdate = UpdateModes.Normalize(update) };
        // The plugin's hooks have fixed timeouts: a wait longer than its Stop hook's would be cut off, and a prompt taken just before could be lost.
        // The wrapper of a plugin version with the long Stop timeout says how long a wait fits; an older one says nothing, and the wait is zero.
        if (config.PluginId is not null && config.StopWait > config.PluginStopWaitMax) config = config with { StopWait = config.PluginStopWaitMax };
        if (!config.InboxFormatFromEnvironment && saved?.InboxFormat is { } format) config = config with { InboxFormat = InboxFormats.Normalize(format) };
        return !config.PushFromEnvironment && saved?.Push is { } push
            ? config with { PushEnabled = push, PushTransport = PushTransports.Normalize(saved.PushTransport) }
            : config;
    }

    /// <summary>Saves whether this machine lets the agent update Claude Code (ADR-0006) and returns the configuration that uses it.</summary>
    public static AgentConfig SaveClaudeUpdate(AgentConfig config, bool on)
    {
        ArgumentNullException.ThrowIfNull(config);
        (Identity.Load(config) with { ClaudeUpdate = on }).Save(config);
        return config with { ClaudeUpdateEnabled = on };
    }

    /// <summary>Saves how far this machine lets the agent update itself (off, check, on; ADR-0004) and returns the configuration that uses it.</summary>
    public static AgentConfig SaveAutoUpdate(AgentConfig config, string mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!UpdateModes.IsValid(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        (Identity.Load(config) with { AutoUpdate = mode }).Save(config);
        return config with { AutoUpdate = mode };
    }

    /// <summary>
    /// Saves whether prompts from the web are pushed into an idle session (ADR-0003, ADR-0007) and through which transport (null keeps
    /// the saved one, the inbox when none), and returns the configuration that uses it.
    /// </summary>
    public static AgentConfig SavePush(AgentConfig config, bool on, string? transport = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var identity = Identity.Load(config);
        var chosen = PushTransports.Normalize(transport ?? identity.PushTransport);
        (identity with { Push = on, PushTransport = chosen }).Save(config);
        return config with { PushEnabled = on, PushTransport = chosen };
    }

    /// <summary>Saves what the inbox gets (full text or the one-line notice, ADR-0007 A2) and returns the configuration that uses it.</summary>
    public static AgentConfig SaveInboxFormat(AgentConfig config, string format)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!InboxFormats.IsValid(format)) throw new ArgumentOutOfRangeException(nameof(format));
        (Identity.Load(config) with { InboxFormat = format }).Save(config);
        return config with { InboxFormat = format };
    }

    /// <summary>
    /// Saves what this machine says about itself (ADR-0008 §3a, `cm-agent config role`): an environment and a function from the closed
    /// sets, or null to say nothing. The claim only ever tightens the role an admin set; it never sets one.
    /// </summary>
    public static void SaveRole(AgentConfig config, string? environment, string? function)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!MachineRoles.IsEnvironment(environment)) throw new ArgumentOutOfRangeException(nameof(environment));
        if (!MachineRoles.IsFunction(function)) throw new ArgumentOutOfRangeException(nameof(function));
        (Identity.Load(config) with { ClaimedEnvironment = environment, ClaimedFunction = function }).Save(config);
    }

    /// <summary>Saves the stop wait (0 to <see cref="AgentConfig.WaitMaxSeconds"/>) and returns the configuration that uses it.</summary>
    public static AgentConfig SaveStopWait(AgentConfig config, int seconds)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (seconds is < 0 or > AgentConfig.WaitMaxSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        (Identity.Load(config) with { StopWaitSeconds = seconds }).Save(config);
        return config with { StopWait = TimeSpan.FromSeconds(seconds) };
    }
}
