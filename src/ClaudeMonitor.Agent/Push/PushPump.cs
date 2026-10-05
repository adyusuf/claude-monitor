using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// One pass of the push (ADR-0003), run by the MCP process of a session: find which session this process serves, take the
/// prompts the web sent it, and hand each to <c>send</c> (a "claude/channel" notification). Nothing here talks to the
/// network: the daemon's stream already put the prompt in the local database. Never involves the model.
/// </summary>
public sealed class PushPump(AgentConfig config, LocalStore store, TimeProvider clock, int parentPid, string? environmentSession)
{
    /// <summary>The session this process serves: the latest one a hook saw under the same Claude Code process, else the one in the environment.</summary>
    public string? Session() => (parentPid > 0 ? store.SessionOf(parentPid) : null) ?? environmentSession;

    /// <summary>Pushes what is waiting. Returns the number of messages written.</summary>
    public async Task<int> PumpAsync(Func<ChannelEnvelope, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var now = clock.GetUtcNow();
        store.RequeueStalePushes(now, config.PushConfirmWait);
        var session = config.PushScope == PushScopes.Machine ? null : Session();
        if (session is null && config.PushScope != PushScopes.Machine) return 0; // not bound yet: the hooks still deliver
        var pushed = 0;
        foreach (var command in store.TakePromptsForPush(session, CommandKinds.Prompt, now))
        {
            try
            {
                await send(ChannelEnvelopes.Build(command, config.PushContentMax, now));
                store.Set(PushStatus.LastPushKey, command.Id);
                store.Set(PushStatus.LastPushAtKey, now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                pushed++;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                store.PushFailed(command.Id); // not written: back to the queue for the next pass or a hook
            }
        }

        return pushed;
    }
}
