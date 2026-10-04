using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>
/// Puts one captured thing in the outbox: masked for secrets unless the workspace switched that off (unknown means
/// on), and capped at the workspace's event size, beyond which a marker with a preview is kept instead.
/// </summary>
public sealed class Recorder(AgentConfig config, LocalStore store)
{
    public const int PreviewChars = 4000;

    public void Record(string kind, string session, JsonNode payload, ProjectInfo? project, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var mask = store.Get("settings.mask_secrets") != "false";
        var max = int.TryParse(store.Get("settings.event_max_bytes"), out var m) ? m : config.EventMaxBytesDefault;
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
