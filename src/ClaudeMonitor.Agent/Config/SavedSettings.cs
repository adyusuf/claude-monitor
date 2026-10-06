using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// The settings a person can change after the fact without editing the environment of every hook process: how long the
/// Stop hook waits for a prompt from the web (`cm-agent install --stop-wait`) and whether prompts are pushed into an idle
/// session (`--push`). They are kept in agent.json beside the identity. CM_STOP_WAIT and CM_PUSH, when set, win (tests and
/// one-off overrides); the defaults stay 0 and off.
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

        if (!config.AutoUpdateFromEnvironment && saved?.AutoUpdate is { } update) config = config with { AutoUpdate = UpdateModes.Normalize(update) };
        return !config.PushFromEnvironment && saved?.Push is { } push ? config with { PushEnabled = push } : config;
    }

    /// <summary>Saves how far this machine lets the agent update itself (off, check, on; ADR-0004) and returns the configuration that uses it.</summary>
    public static AgentConfig SaveAutoUpdate(AgentConfig config, string mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!UpdateModes.IsValid(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        (Identity.Load(config) with { AutoUpdate = mode }).Save(config);
        return config with { AutoUpdate = mode };
    }

    /// <summary>Saves whether prompts from the web are pushed into an idle session (ADR-0003) and returns the configuration that uses it.</summary>
    public static AgentConfig SavePush(AgentConfig config, bool on)
    {
        ArgumentNullException.ThrowIfNull(config);
        (Identity.Load(config) with { Push = on }).Save(config);
        return config with { PushEnabled = on };
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
