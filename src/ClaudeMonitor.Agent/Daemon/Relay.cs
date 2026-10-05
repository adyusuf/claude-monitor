using System.Globalization;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// One pass of the daemon's chores, each callable on its own (and tested so): upload the outbox, hand permission
/// requests to the API, report commands the hooks applied, keep the workspace's settings, and take what arrives on
/// the agent's stream.
/// </summary>
public sealed class Relay(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock)
{
    /// <summary>When the API last answered a heartbeat (ISO-8601 UTC): the hooks wait for the web only if it is recent.</summary>
    public const string LastContactKey = "relay.last_contact";

    /// <summary>Tells the API this agent is alive; only an answered heartbeat counts as contact.</summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        await api.HeartbeatAsync(ct);
        store.Set(LastContactKey, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Sends batches until the outbox is empty. Returns the number of events the API acknowledged.</summary>
    public async Task<int> UploadAsync(CancellationToken ct)
    {
        var sent = 0;
        while (store.NextBatch(config.BatchEvents, config.BatchBytes) is { } batch)
        {
            var ack = await api.SendBatchAsync(new EventBatch(batch.Seq, batch.Rows.Select(r => r.Event).ToList()), ct);
            store.Acknowledge(batch.Seq);
            sent += ack.Duplicate ? 0 : batch.Rows.Count;
        }

        return sent;
    }

    /// <summary>New permission requests go to the API; ones past their wait are given up (Claude asks locally).</summary>
    public async Task PermissionsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var p in store.PermissionsIn(PermissionStates.New))
        {
            var left = p.WaitSeconds - (int)(now - p.CreatedAt).TotalSeconds;
            if (left <= 1)
            {
                store.PermissionExpired(p.LocalId);
                continue;
            }

            await UploadAsync(ct); // the session must exist on the API before a request can name it
            using var input = JsonDocument.Parse(p.ToolInput);
            var created = await api.CreatePermissionAsync(
                new PermissionRequestCreate(p.Harness, p.Session, p.ToolName, input.RootElement.Clone(), left), ct);
            if (created is not null) store.PermissionSent(p.LocalId, created.Id.ToString());
        }
    }

    /// <summary>A command a hook took has been applied to its session: the web hears so.</summary>
    public async Task ReportCommandsAsync(CancellationToken ct)
    {
        foreach (var taken in store.TakenCommands())
        {
            await api.CommandStatusAsync(taken.Id, CommandStatuses.Applied, null, ct, taken.At);
            store.MarkCommandReported(taken.Id);
        }
    }

    public async Task SettingsAsync(CancellationToken ct)
    {
        var s = await api.SettingsAsync(ct);
        store.Set("settings.mask_secrets", s.MaskSecrets ? "true" : "false");
        store.Set("settings.event_max_bytes", s.EventMaxBytes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Takes one stream message. Returns false when the agent was revoked and must stop.</summary>
    public async Task<bool> OnStreamAsync(string name, JsonElement data, CancellationToken ct)
    {
        switch (name)
        {
            case AgentStreamEvents.Command:
                var c = data.Deserialize<AgentCommandMessage>(ApiClient.Json)!;
                store.SaveCommand(new LocalCommand(c.Id.ToString(), c.SessionExternalId, c.Kind, c.Body, c.ExpiresAt));
                await api.CommandStatusAsync(c.Id.ToString(), CommandStatuses.Delivered, null, ct);
                return true;
            case AgentStreamEvents.PermissionAnswer:
                var a = data.Deserialize<PermissionAnswerMessage>(ApiClient.Json)!;
                store.PermissionAnswered(a.Id.ToString(), a.Decision, a.Reason);
                return true;
            case AgentStreamEvents.Revoked:
                return false;
            default:
                return true;
        }
    }
}
