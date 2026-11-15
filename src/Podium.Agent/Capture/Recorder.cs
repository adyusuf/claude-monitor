using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Capture;

/// <summary>
/// Puts one captured thing in the outbox: masked for secrets unless the workspace switched that off (unknown means
/// on), and capped at the workspace's event size, beyond which a marker with a preview is kept instead. Both switches are
/// read through the workspace tag (WorkspaceSettings): one read for another workspace counts as unread, so masking is on.
/// </summary>
public sealed class Recorder(AgentConfig config, LocalStore store)
{
    public const int PreviewChars = 4000;

    /// <summary>The workspace's "mask secrets" switch as the daemon last read it ("false" turns masking off).</summary>
    public const string MaskSecretsKey = "settings.mask_secrets";

    /// <summary>The workspace's event size cap, in bytes, as the daemon last read it.</summary>
    public const string EventMaxBytesKey = "settings.event_max_bytes";

    // Read once per recorder (one hook run, one transcript in one tailer pass): agent.json is read for the tag.
    private (bool Mask, int Max)? switches;

    public void Record(string kind, string session, JsonNode payload, ProjectInfo? project, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var (mask, max) = switches ??= (WorkspaceSettings.Get(config, store, MaskSecretsKey) != "false",
            int.TryParse(WorkspaceSettings.Get(config, store, EventMaxBytesKey), out var m) ? m : config.EventMaxBytesDefault);
        var node = mask ? Masker.Mask(payload.DeepClone())! : payload;
        var json = node.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(json);
        var truncated = bytes > max;
        if (truncated)
        {
            json = new JsonObject
            {
                ["truncated"] = true,
                ["bytes"] = bytes,
                ["hook_event_name"] = payload["hook_event_name"]?.DeepClone(),
                ["tool_name"] = payload["tool_name"]?.DeepClone(),
                ["preview"] = json[..Math.Min(json.Length, PreviewChars)],
            }.ToJsonString();
        }

        using var doc = JsonDocument.Parse(json);
        store.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, session, kind, now, doc.RootElement.Clone(), truncated,
            project?.Key, project?.Name, project?.Branch), json);
    }
}
