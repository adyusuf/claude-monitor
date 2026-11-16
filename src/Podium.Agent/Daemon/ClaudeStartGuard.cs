using Podium.Agent.Config;
using Podium.Agent.Storage;

namespace Podium.Agent.Daemon;

/// <summary>
/// The agent's own check before it starts Claude or changes its setup because the web asked (ADR-0014, F-201). The API already checks the workspace's remote-work
/// switch and the owner's opt-in; the agent checks them again from what its settings pass last stored, so an action that was queued before the owner turned the
/// opt-in off, or a workspace that has since switched remote work off, is refused here too. Fail-closed: a value that was never read, or belongs to another
/// workspace, refuses. A server that does not tell the opt-in (<see cref="Unknown"/>, an older API) leaves that one to the API, as before.
/// </summary>
public static class ClaudeStartGuard
{
    /// <summary>What is stored for an opt-in the server did not say.</summary>
    public const string Unknown = "unknown";

    /// <summary>The value the settings pass stores for the opt-in the API told (null: it did not say).</summary>
    public static string Stored(bool? claudeControl) => claudeControl switch
    {
        true => "true",
        false => "false",
        _ => Unknown,
    };

    /// <summary>Why this agent will not act, or null when the stored switches allow it.</summary>
    public static string? Refusal(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        if (WorkspaceSettings.Get(config, store, MachineMonitor.RemoteRunsKey) != "true")
        {
            return "refused on this machine: remote work is not switched on for its workspace (as this agent last read it)";
        }

        return WorkspaceSettings.Get(config, store, MachineMonitor.ClaudeControlKey) switch
        {
            "true" or Unknown => null,
            _ => "refused on this machine: the owner's switch for Claude control is not on (as this agent last read it)",
        };
    }
}
