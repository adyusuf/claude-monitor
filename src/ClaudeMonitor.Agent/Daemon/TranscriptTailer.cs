using System.Text;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// Follows the transcript (JSONL) of every session a hook mentioned in the last day: each new complete line becomes a
/// "transcript" event, and each assistant message's token usage a "usage" event, once per message id (a message is
/// written in several lines that repeat its usage).
/// </summary>
public sealed class TranscriptTailer(AgentConfig config, LocalStore store, TimeProvider clock)
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(1);

    /// <summary>Ids of prompts pushed into a session and not yet seen in a transcript: the first line that holds one confirms it (ADR-0003).</summary>
    private List<string> pending = [];

    /// <summary>Reads what was appended since last time. Returns the number of lines taken.</summary>
    public int RunOnce()
    {
        var taken = 0;
        pending = store.UnconfirmedPushes().Select(p => p.Id).ToList();
        foreach (var t in store.Transcripts(clock.GetUtcNow() - ActiveWindow))
        {
            taken += Follow(t);
        }

        return taken;
    }

    private int Follow(TranscriptCursor t)
    {
        if (!File.Exists(t.Path)) return 0;
        using var stream = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = t.Offset > stream.Length ? 0 : t.Offset; // a shorter file was replaced: start over
        stream.Seek(offset, SeekOrigin.Begin);
        var project = t.ProjectKey is null ? null : new ProjectInfo(t.ProjectKey, t.ProjectName ?? t.ProjectKey, t.GitBranch);
        var recorder = new Recorder(config, store);
        var buffer = new MemoryStream();
        var taken = 0;
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b != '\n')
            {
                if (buffer.Length < config.TranscriptLineMax) buffer.WriteByte((byte)b);
                continue;
            }

            offset = stream.Position;
            var line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            buffer.SetLength(0);
            if (Take(t, line, project, recorder)) taken++;
        }

        store.TranscriptRead(t.Session, offset); // a partial last line is read again next time
        return taken;
    }

    private bool Take(TranscriptCursor t, string line, ProjectInfo? project, Recorder recorder)
    {
        JsonNode? entry;
        try
        {
            entry = JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (entry is not JsonObject obj) return false;
        var now = clock.GetUtcNow();
        // The wrapper's own text, not the bare id: `monitor_status` also prints ids, and that must not confirm a push.
        foreach (var pushed in pending.Where(p => line.Contains($"{ChannelEnvelopes.Open} id={p}", StringComparison.Ordinal)).ToList())
        {
            if (store.ConfirmPush(pushed, now)) pending.Remove(pushed);
        }

        recorder.Record(EventKinds.Transcript, t.Session, obj, project, now);
        if (obj["type"]?.GetValue<string>() == "assistant" && obj["message"] is JsonObject message
            && message["usage"] is JsonObject usage && message["model"]?.GetValue<string>() is { } model
            && (message["id"]?.GetValue<string>() ?? obj["uuid"]?.GetValue<string>()) is { } id && store.FirstSight(t.Session, id))
        {
            recorder.Record(EventKinds.Usage, t.Session, Usage(id, model, usage), project, now);
        }

        return true;
    }

    /// <summary>Claude's usage block, with the cache writes split by lifetime (they are priced differently).</summary>
    internal static JsonObject Usage(string messageId, string model, JsonObject usage)
    {
        long N(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        var creation = usage["cache_creation"] as JsonObject;
        var w5 = creation is null ? N(usage["cache_creation_input_tokens"]) : N(creation["ephemeral_5m_input_tokens"]);
        var w1 = creation is null ? 0 : N(creation["ephemeral_1h_input_tokens"]);
        return new JsonObject
        {
            ["messageId"] = messageId,
            ["model"] = model,
            ["inputTokens"] = N(usage["input_tokens"]),
            ["outputTokens"] = N(usage["output_tokens"]),
            ["cacheReadTokens"] = N(usage["cache_read_input_tokens"]),
            ["cacheWrite5mTokens"] = w5,
            ["cacheWrite1hTokens"] = w1,
        };
    }
}
