using ClaudeMonitor.Agent.Auth;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// The one setting a person can change after the fact without editing the environment of every hook process:
/// how long the Stop hook waits for a prompt from the web (`cm-agent install --stop-wait`). It is kept in agent.json
/// beside the identity. CM_STOP_WAIT, when set, wins (tests and one-off overrides); the default stays 0.
/// </summary>
public static class SavedSettings
{
    /// <summary>The configuration with the saved stop wait applied; unchanged when CM_STOP_WAIT is set or nothing is saved.</summary>
    public static AgentConfig Apply(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.StopWaitFromEnvironment || Identity.Peek(config)?.StopWaitSeconds is not { } seconds
            ? config
            : config with { StopWait = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, AgentConfig.WaitMaxSeconds)) };
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
