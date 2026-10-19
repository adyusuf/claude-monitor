using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// The configuration with the saved settings applied, re-read at most every <see cref="AgentConfig.PushSettingsEvery"/>: a long-running
/// daemon then follows `aipod install --push on|off` without a restart.
/// </summary>
public sealed class LiveSettings(AgentConfig config, TimeProvider clock)
{
    private AgentConfig current = Config.SavedSettings.Apply(config);
    private DateTimeOffset readAt = clock.GetUtcNow();

    public AgentConfig Current()
    {
        var now = clock.GetUtcNow();
        if (now - readAt >= config.PushSettingsEvery)
        {
            current = Config.SavedSettings.Apply(config);
            readAt = now;
        }

        return current;
    }
}
