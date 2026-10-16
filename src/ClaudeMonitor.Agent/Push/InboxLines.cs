using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// The lines written to a session's inbox socket (ADR-0007, measured in §8 of the ADR on Claude Code 2.1.294/2.1.299). The shape is Claude
/// Code's own user-message line, not a documented contract: this is the one file that knows it.
/// </summary>
public static class InboxLines
{
    public const string From = "claude-monitor";

    /// <summary>
    /// The auth line (always sent when the session's token is known: it is what a session in bypassPermissions mode needs to deliver
    /// rather than hold the message, and Windows requires it) followed by the message line. `session_id` makes a stale socket harmless:
    /// Claude Code drops a line addressed to another session. `uuid` is the command id when it is a GUID.
    /// </summary>
    public static IReadOnlyList<string> For(LocalCommand command, string? token, string content)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["from"] = From,
            ["session_id"] = command.Session,
        };
        if (Guid.TryParse(command.Id, out var id)) message["uuid"] = id.ToString();
        var lines = new List<string>(2);
        if (token is { Length: > 0 }) lines.Add(new JsonObject { ["type"] = "auth", ["token"] = token }.ToJsonString(Compact));
        lines.Add(message.ToJsonString(Compact));
        return lines;
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
