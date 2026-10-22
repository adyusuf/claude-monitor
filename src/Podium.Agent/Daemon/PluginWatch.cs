using Podium.Agent.Config;

namespace Podium.Agent.Daemon;

/// <summary>
/// Whether the Claude Code plugin that started this daemon is still there (ADR-0007). Claude Code runs nothing when a plugin is uninstalled, so
/// the daemon looks for the two traces it does leave: the plugin's data directory is deleted on uninstall, and a plugin that is switched off
/// is <c>false</c> in the user's <c>enabledPlugins</c>. Two more signals come from Claude Code's plugin list (<see cref="PluginRegistry"/>): a plugin
/// no longer listed was uninstalled even when its data was kept (<c>--keep-data</c>), and a plugin that is switched off is only gone when it is off
/// everywhere it is installed, so one project switching it off does not stop it for the others. Anything unreadable counts as "still there": a daemon
/// that stays up does no harm, one that stops by mistake silences the machine.
/// </summary>
public static class PluginWatch
{
    /// <summary>Why the daemon should stop, or null. <paramref name="dataExistedAtStart"/>: the data directory was there when the daemon started (else its absence means nothing).</summary>
    public static string? Gone(AgentConfig config, bool dataExistedAtStart)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.PluginDataDir is { } data && dataExistedAtStart && !Directory.Exists(data)) return "the plugin's data directory is gone: the plugin was uninstalled";
        if (config.PluginId is not { } id) return null;
        var installs = PluginRegistry.Installs(config, id);
        if (installs is { Count: 0 }) return "Claude Code's plugin list no longer has the plugin: it was uninstalled and its data kept";
        if (installs is null ? Disabled(config, id) : PluginRegistry.AllDisabled(config, id, installs)) return "Claude Code has the plugin switched off";
        return null;
    }

    /// <summary>Ends the daemon (through <paramref name="cts"/>) when its plugin is uninstalled or switched off. Its events stay in the local database for the next daemon.</summary>
    public static async Task RunAsync(AgentConfig config, AgentLog log, CancellationTokenSource cts, bool dataExisted)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(cts);
        if (config.PluginDataDir is null && config.PluginId is null) return; // not started by the plugin: nothing to watch
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(config.PluginWatchEvery, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Gone(config, dataExisted) is { } why)
            {
                log.Write($"stopping: {why}");
                await cts.CancelAsync();
                return;
            }
        }
    }

    public static bool DataExists(AgentConfig config) => config.PluginDataDir is { } data && Directory.Exists(data);

    /// <summary>True when the user's settings say <c>enabledPlugins[id]</c> is exactly false (the signal used when Claude Code's plugin list cannot be read).</summary>
    public static bool Disabled(AgentConfig config, string id)
    {
        return PluginRegistry.Flag(Path.Combine(config.ClaudeConfigDir, "settings.json"), id) == false;
    }
}
