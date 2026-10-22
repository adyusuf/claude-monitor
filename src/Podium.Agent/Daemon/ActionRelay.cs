using System.Collections.Concurrent;
using Podium.Agent.Config;
using Podium.Agent.Net;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

/// <summary>
/// The fixed actions its owner asked for on the web (ADR-0005, "Agent actions from the web"). An action is only a kind from a closed set and the
/// daemon performs it with its own code, never with text from the web: <c>status</c> is what `aipod status` prints, <c>update_check</c> the
/// updater's check, <c>update</c> and <c>restart</c> start the same detached `aipod` verb the person would type (it ends this daemon and starts
/// the next). The API hands an action out once: the first report ("delivered") is a compare-and-set, so a replayed message is not performed twice.
/// Because those two end this daemon, their outcome is reported BEFORE the verb starts and says "started". Nothing here throws into the stream.
/// </summary>
public sealed class ActionRelay(ApiClient api, AgentLog log, Func<Task<string>> status, Func<CancellationToken, Task<UpdateCheck>> check,
    Func<string, bool> spawn, bool serviceMode = false, ClaudeControl? claude = null, ClaudeConfigControl? claudeConfig = null)
{
    private readonly ConcurrentDictionary<Guid, byte> taken = new();

    /// <summary>Takes an action from the stream; it is performed in the background. False when this agent already has it in hand.</summary>
    public bool Accept(AgentActionMessage action, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!taken.TryAdd(action.Id, 0)) return false;
        _ = Task.Run(() => PerformAsync(action, stop), CancellationToken.None);
        return true;
    }

    internal async Task PerformAsync(AgentActionMessage action, CancellationToken ct)
    {
        try
        {
            if (!await api.ActionStatusAsync(action.Id, AgentActionStatuses.Delivered, null, ct)) return; // taken elsewhere, finished or expired
            var (succeeded, text, then, sessionRef) = await DoAsync(action, ct);
            try
            {
                await api.ActionStatusAsync(action.Id, succeeded ? AgentActionStatuses.Succeeded : AgentActionStatuses.Failed, Cap(text), ct, sessionRef);
            }
            catch (Exception e) when (then is not null && e is HttpRequestException or TimeoutException or ApiException)
            {
                // The owner asked for the restart or update and the API took the action: a report that cannot be sent is no reason not to do it.
                log.Write($"action {action.Kind} not reported: {e.GetType().Name}");
            }

            if (then is not null && !spawn(then)) log.Write($"action {action.Kind}: aipod {then} could not be started");
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or ApiException or OperationCanceledException)
        {
            log.Write($"action {action.Kind} not reported: {e.GetType().Name}");
        }
        catch (Exception e)
        {
            log.Write($"action {action.Kind} failed in the agent: {e.GetType().Name}");
            try
            {
                await api.ActionStatusAsync(action.Id, AgentActionStatuses.Failed, $"failed in the agent: {e.GetType().Name}", CancellationToken.None);
            }
            catch (Exception report) when (report is HttpRequestException or TimeoutException or ApiException or OperationCanceledException)
            {
                log.Write($"action {action.Kind} failure not reported: {report.GetType().Name}");
            }
        }
        finally
        {
            taken.TryRemove(action.Id, out _);
        }
    }

    /// <summary>What to do for a kind: whether it worked, the text to show, and the `aipod` verb to start once that is reported (null for none).</summary>
    private async Task<(bool Succeeded, string Text, string? Then, string? SessionRef)> DoAsync(AgentActionMessage action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case AgentActionKinds.ClaudeStart when claude is not null:
                var started = await claude.StartAsync(action.ProjectKey, action.PermissionMode, ct, action.FirstPrompt, action.Id);
                return (started.Succeeded, started.Text, null, started.SessionRef);
            case AgentActionKinds.ClaudeStop when claude is not null:
                var stopped = await claude.StopAsync(action.SessionRef, ct);
                return (stopped.Succeeded, stopped.Text, null, null);
            case AgentActionKinds.ClaudeStart:
            case AgentActionKinds.ClaudeStop:
                return (false, "this agent cannot start or stop Claude here", null, null);
            case AgentActionKinds.PluginEnable or AgentActionKinds.PluginDisable or AgentActionKinds.PluginUpdate or AgentActionKinds.PluginInstall when claudeConfig is not null:
                var plugin = await claudeConfig.PluginAsync(action.Kind, action.Target, ct);
                return (plugin.Succeeded, plugin.Text, null, null);
            case AgentActionKinds.ProjectSetting when claudeConfig is not null:
                var setting = claudeConfig.Setting(action.ProjectKey, action.Target, action.Value);
                return (setting.Succeeded, setting.Text, null, null);
            case AgentActionKinds.PluginEnable or AgentActionKinds.PluginDisable or AgentActionKinds.PluginUpdate or AgentActionKinds.PluginInstall or AgentActionKinds.ProjectSetting:
                return (false, "this agent cannot change Claude's setup here", null, null);
            case AgentActionKinds.Status:
                return (true, await status(), null, null);
            case AgentActionKinds.UpdateCheck:
                var seen = await check(ct);
                return (!seen.Refused, Describe(seen), null, null);
            case AgentActionKinds.Update when serviceMode:
                return (false, "this agent runs as a service: an administrator upgrades it (its binary is not writable by the service account)", null, null);
            case AgentActionKinds.Update:
                var offer = await check(ct);
                return offer.Offer is null
                    ? (!offer.Refused, Describe(offer), null, null)
                    : (true, $"updating to {offer.Offer.Version} (running {AgentConfig.Version}); the agent restarts and reports the new version", UpdateCommand.Verb, null);
            case AgentActionKinds.Restart:
                return (true, "restarting the agent", "restart", null);
            default:
                return (false, "unknown action", null, null);
        }
    }

    private static string Describe(UpdateCheck seen) => seen.Offer is { } offer
        ? $"update available: {offer.Version} (running {AgentConfig.Version})"
        : seen.Refused ? $"update refused: {seen.Code}: {seen.Message}" : $"{seen.Message} ({AgentConfig.Version})";

    private static string Cap(string text) => text.Length > AgentActionKinds.ResultMax ? text[..AgentActionKinds.ResultMax] : text;
}
