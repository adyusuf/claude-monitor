using ClaudeMonitor.Agent.Auth;

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

        return !config.PushFromEnvironment && saved?.Push is { } push ? config with { PushEnabled = push } : config;
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
