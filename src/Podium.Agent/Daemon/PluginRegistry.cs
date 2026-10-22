using System.Text.Json.Nodes;
using Podium.Agent.Config;

namespace Podium.Agent.Daemon;

/// <summary>One place Claude Code has a plugin installed: the user's account, or a project (shared or local to this machine).</summary>
public sealed record PluginInstall(string Scope, string? ProjectPath);

/// <summary>
/// What Claude Code's own plugin list and settings say about the plugin that started this daemon (ADR-0007, B5). The list is
/// <c>plugins/installed_plugins.json</c> in the config folder: <c>{"version": 2, "plugins": {"name@marketplace": [{"scope", "projectPath", ...}]}}</c>.
/// It is not a documented interface, so it is only believed when it has exactly that shape; anything else is "unknown" and the daemon falls back to
/// the signals it had before. Whether the plugin is switched off is read where Claude Code reads it: <c>enabledPlugins</c> in the user's settings, and for
/// a project install in that project's <c>.claude/settings.local.json</c>, then <c>.claude/settings.json</c>, then the user's. One project switching it off
/// never stops a daemon that other projects, or the user's account, still use.
/// </summary>
public static class PluginRegistry
{
    private const int KnownVersion = 2;
    private const string User = "user";
    private const string Project = "project";
    private const string Local = "local";

    /// <summary>The installs of <paramref name="id"/>; empty when the list is readable and has none (uninstalled); null when the list cannot be believed.</summary>
    public static IReadOnlyList<PluginInstall>? Installs(AgentConfig config, string id)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            var path = Path.Combine(config.ClaudeConfigDir, "plugins", "installed_plugins.json");
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["version"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != KnownVersion
                || root["plugins"] is not JsonObject plugins)
            {
                return null;
            }

            if (!plugins.TryGetPropertyValue(id, out var entry) || entry is null) return [];
            if (entry is not JsonArray list) return null;
            var installs = new List<PluginInstall>();
            foreach (var item in list)
            {
                if (item is not JsonObject install) return null;
                installs.Add(new PluginInstall(install["scope"]?.GetValue<string>() ?? User, install["projectPath"]?.GetValue<string>()));
            }

            return installs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>True when every install is switched off where Claude Code would read it. No installs: false.</summary>
    public static bool AllDisabled(AgentConfig config, string id, IReadOnlyList<PluginInstall> installs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        return installs.Count > 0 && installs.All(i => Disabled(config, id, i));
    }

    private static bool Disabled(AgentConfig config, string id, PluginInstall install)
    {
        var user = Flag(Path.Combine(config.ClaudeConfigDir, "settings.json"), id);
        if (install.Scope is not (Project or Local) || string.IsNullOrEmpty(install.ProjectPath)) return user == false; // an install for the account: a project's switch does not count
        var folder = Path.Combine(install.ProjectPath, ".claude");
        var value = Flag(Path.Combine(folder, "settings.local.json"), id) ?? Flag(Path.Combine(folder, "settings.json"), id) ?? user;
        return value == false;
    }

    /// <summary><c>enabledPlugins[id]</c> in a settings file: true, false, or null when the file or the value is missing, unreadable or not a boolean.</summary>
    public static bool? Flag(string settingsPath, string id)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            return JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root && root["enabledPlugins"] is JsonObject enabled
                && enabled[id] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
