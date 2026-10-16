using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// Whether the Claude Code plugin that started this daemon is still there (ADR-0007). Claude Code runs nothing when a plugin is uninstalled, so
/// the daemon looks for the two traces it does leave: the plugin's data directory is deleted on uninstall, and a plugin that is switched off
/// is <c>false</c> in the user's <c>enabledPlugins</c>. Anything unreadable counts as "still there": a daemon that stays up does no harm, one that
/// stops by mistake silences the machine.
/// </summary>
public static class PluginWatch
{
    /// <summary>Why the daemon should stop, or null. <paramref name="dataExistedAtStart"/>: the data directory was there when the daemon started (else its absence means nothing).</summary>
    public static string? Gone(AgentConfig config, bool dataExistedAtStart)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.PluginDataDir is { } data && dataExistedAtStart && !Directory.Exists(data)) return "the plugin's data directory is gone: the plugin was uninstalled";
        if (config.PluginId is { } id && Disabled(config, id)) return "Claude Code has the plugin switched off";
        return null;
    }

    public static bool DataExists(AgentConfig config) => config.PluginDataDir is { } data && Directory.Exists(data);

    /// <summary>True when the user's settings say <c>enabledPlugins[id]</c> is exactly false.</summary>
    public static bool Disabled(AgentConfig config, string id)
    {
        try
        {
            var path = Path.Combine(config.ClaudeConfigDir, "settings.json");
            if (!File.Exists(path)) return false;
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["enabledPlugins"] is JsonObject enabled
                && enabled[id] is JsonValue value && value.TryGetValue<bool>(out var on) && !on;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
