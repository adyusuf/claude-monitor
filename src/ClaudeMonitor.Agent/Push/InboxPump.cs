using System.Globalization;
using System.Net.Sockets;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// One pass of the push through the session inboxes (ADR-0007), run by the daemon every <see cref="AgentConfig.PushPollEvery"/>: for every
/// session whose socket a hook stored, take the prompts the web sent it and write each to the socket. Nothing here involves the model.
/// A write that fails is not retried in a loop: the prompt goes back to the queue for the hooks, the session's inbox is forgotten until a
/// hook says where it listens again, and the failure is recorded (an exception type name only, never content or the token).
/// </summary>
public sealed class InboxPump(Func<AgentConfig> settings, LocalStore store, IInboxTransport transport, TimeProvider clock)
{
    /// <summary>Inboxes no hook has refreshed for this long are forgotten (a session that crashed without a SessionEnd).</summary>
    public static readonly TimeSpan InboxLifetime = TimeSpan.FromDays(1);

    /// <summary>Pushes what is waiting. Returns the number of prompts written.</summary>
    public async Task<int> PumpAsync(CancellationToken ct)
    {
        var config = settings();
        if (!config.InboxPushEnabled) return 0;
        var now = clock.GetUtcNow();
        store.RequeueStalePushes(now, config.PushConfirmWait); // a prompt the session never showed goes back to the hooks
        store.PruneInboxes(now - InboxLifetime);
        var pushed = 0;
        foreach (var inbox in store.Inboxes())
        {
            var prompts = store.TakePromptsForPush(inbox.Session, CommandKinds.Prompt, now);
            for (var i = 0; i < prompts.Count; i++)
            {
                try
                {
                    var content = ChannelEnvelopes.BuildInbox(prompts[i], config.PushContentMax, now);
                    await transport.SendAsync(inbox.Endpoint, InboxLines.For(prompts[i], inbox.Token, content), config.InboxConnectTimeout, ct);
                    store.Set(PushStatus.LastPushKey, prompts[i].Id);
                    store.Set(PushStatus.LastPushAtKey, now.ToString("O", CultureInfo.InvariantCulture));
                    pushed++;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or SocketException or TimeoutException
                                              or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
                {
                    for (var j = i; j < prompts.Count; j++) store.PushFailed(prompts[j].Id); // not written: back to the queue for a hook
                    store.DropInbox(inbox.Session);
                    store.Set(LocalStore.InboxErrorKey, e.GetType().Name);
                    store.Set(LocalStore.InboxErrorAtKey, now.ToString("O", CultureInfo.InvariantCulture));
                    break;
                }
            }
        }

        return pushed;
    }
}
