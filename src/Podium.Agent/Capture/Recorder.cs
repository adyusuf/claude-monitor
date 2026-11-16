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
/// on), and cleaned of what the API's database cannot store (NUL, half a surrogate pair), and capped at the workspace's event size, beyond which a marker with a preview is kept instead. Both switches are
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

    /// <summary>Records one thing; <paramref name="cut"/> marks a payload that is already a marker for something cut before it got here.</summary>
    public void Record(string kind, string session, JsonNode payload, ProjectInfo? project, DateTimeOffset now, bool cut = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var (mask, max) = switches ??= (WorkspaceSettings.Get(config, store, MaskSecretsKey) != "false",
            int.TryParse(WorkspaceSettings.Get(config, store, EventMaxBytesKey), out var m) ? m : config.EventMaxBytesDefault);
        var cleaned = SafeText.Clean(payload)!; // a copy: what the API's database cannot store (NUL, a lone surrogate) is replaced before it is queued
        var node = mask ? Masker.Mask(cleaned)! : cleaned;
        var json = node.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(json);
        var truncated = cut || bytes > max;
        if (bytes > max)
        {
            json = new JsonObject
            {
                ["truncated"] = true,
                ["bytes"] = bytes,
                ["hook_event_name"] = cleaned["hook_event_name"]?.DeepClone(),
                ["tool_name"] = cleaned["tool_name"]?.DeepClone(),
                ["preview"] = json[..Math.Min(json.Length, PreviewChars)],
            }.ToJsonString();
        }

        using var doc = JsonDocument.Parse(json);
        store.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, session, kind, now, doc.RootElement.Clone(), truncated,
            project?.Key, project?.Name, project?.Branch), json);
    }
}
