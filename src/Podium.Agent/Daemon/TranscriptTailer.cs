using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Podium.Agent.Capture;
using Podium.Agent.Config;
using Podium.Agent.Push;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

/// <summary>
/// Follows the transcript (JSONL) of every session a hook mentioned in the last day: each new complete line becomes a
/// "transcript" event, and each assistant message's token usage a "usage" event, once per message id (a message is
/// written in several lines that repeat its usage). One transcript that cannot be read, or one line that cannot be taken,
/// never stops the other transcripts: the failure is logged by type name and that transcript goes on at the next pass.
/// </summary>
public sealed class TranscriptTailer(AgentConfig config, LocalStore store, TimeProvider clock, AgentLog log)
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(1);

    /// <summary>Ids of prompts pushed into a session and not yet seen in a transcript: the first line that holds one confirms it (ADR-0003).</summary>
    private List<string> pending = [];

    /// <summary>Reads what was appended since last time. Returns the number of lines taken.</summary>
    public int RunOnce()
    {
        var taken = 0;
        pending = [.. store.UnconfirmedPushes().Select(p => p.Id), .. store.UnconfirmedAsks().Select(a => a.Id)];
        foreach (var t in store.Transcripts(clock.GetUtcNow() - ActiveWindow))
        {
            try
            {
                taken += Follow(t);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or DbException)
            {
                log.Write($"transcript of session {t.Session} not read: {e.GetType().Name}"); // never its path or content
            }
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
        long lineBytes = 0;
        int b;
        try
        {
            while ((b = stream.ReadByte()) != -1)
            {
                if (b != '\n')
                {
                    lineBytes++;
                    if (buffer.Length < config.TranscriptLineMax) buffer.WriteByte((byte)b);
                    continue;
                }

                var lineStart = offset;
                offset = stream.Position;
                var line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                buffer.SetLength(0);
                var bytes = lineBytes;
                lineBytes = 0;
                try
                {
                    if (bytes > config.TranscriptLineMax)
                    {
                        // Only its first part was kept, which cannot be parsed: a marker says that a line was there and how big it was.
                        recorder.Record(EventKinds.Transcript, t.Session, Oversized(line, bytes), project, clock.GetUtcNow(), cut: true);
                        taken++;
                    }
                    else if (Take(t, line, project, recorder))
                    {
                        taken++;
                    }
                }
                catch (DbException)
                {
                    offset = lineStart; // a busy or locked database is not the line's fault: it is read again at the next pass, not lost
                    throw;
                }
            }
        }
        finally
        {
            // A partial last line is read again next time; the lines taken before a failure are not, nor a line that failed to parse; a line whose write failed is.
            store.TranscriptRead(t.Session, offset);
        }

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
        // The wrapper's own text, not the bare id: `aipod_status` also prints ids, and that must not confirm a push. An inbox message or an ask opens
        // with the fence; a channel message is the <channel> tag whose message_id attribute holds the id (quoted, or escaped inside the JSON line).
        foreach (var pushed in pending.Where(p => ChannelEnvelopes.AppearsIn(line, p)).ToList())
        {
            if (store.ConfirmPush(pushed, now) || store.ConfirmAsk(pushed, now)) pending.Remove(pushed);
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

    /// <summary>How many times the JSON escapes of a kept fragment are undone: content nested in content (a tool result that holds a transcript) is escaped once per level.</summary>
    private const int UnescapeLevels = 3;

    /// <summary>A line over the limit: its size and a preview cut on a character boundary. The kept part is raw JSONL text, whose escaped quotes (\"api_key\": \"...\") hide a secret from the masker: the escapes are undone before it is masked.</summary>
    private static JsonObject Oversized(string kept, long bytes)
    {
        var text = kept;
        for (var level = 0; level < UnescapeLevels && text.Contains('\\', StringComparison.Ordinal); level++) text = UnescapeJson(text);
        var n = Math.Min(text.Length, Recorder.PreviewChars);
        if (n < text.Length && char.IsHighSurrogate(text[n - 1])) n--;
        return new JsonObject { ["truncated"] = true, ["bytes"] = bytes, ["preview"] = text[..n] };
    }

    /// <summary>Undoes one level of JSON string escapes in a fragment that may end in the middle of one; a backslash that starts no known escape stays as it is.</summary>
    internal static string UnescapeJson(string fragment)
    {
        var sb = new StringBuilder(fragment.Length);
        for (var i = 0; i < fragment.Length; i++)
        {
            if (fragment[i] != '\\' || i + 1 >= fragment.Length)
            {
                sb.Append(fragment[i]);
                continue;
            }

            var next = fragment[i + 1];
            switch (next)
            {
                case '"' or '\\' or '/':
                    sb.Append(next);
                    i++;
                    break;
                case 'n':
                    sb.Append('\n');
                    i++;
                    break;
                case 't':
                    sb.Append('\t');
                    i++;
                    break;
                case 'r':
                    sb.Append('\r');
                    i++;
                    break;
                case 'b':
                    sb.Append('\b');
                    i++;
                    break;
                case 'f':
                    sb.Append('\f');
                    i++;
                    break;
                case 'u' when i + 5 < fragment.Length && ushort.TryParse(fragment.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 5;
                    break;
                default:
                    sb.Append('\\');
                    break;
            }
        }

        return sb.ToString();
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
