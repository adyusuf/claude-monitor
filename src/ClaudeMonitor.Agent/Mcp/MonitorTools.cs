using System.ComponentModel;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;
using ModelContextProtocol.Server;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>
/// The MCP tools the model can call (ADR-0002). Hooks capture everything without the model's help; these let it say
/// something on purpose. Starting the MCP server also wakes the daemon, so a session with the plugin has its agent.
/// </summary>
[McpServerToolType]
public sealed class MonitorTools(AgentConfig config, TimeProvider clock)
{
    public const int NoteMax = 2000;

    [McpServerTool(Name = "monitor_status", ReadOnly = true)]
    [Description("Whether this machine reports to Claude Monitor, which server, and how many events wait to be sent.")]
    public string Status()
    {
        var identity = Identity.Load(config);
        using var store = new LocalStore(config.DatabasePath);
        return identity.Connected
            ? $"Connected to {identity.Server} (workspace {identity.WorkspaceId}). Events waiting to be sent: {store.OutboxCount()}."
            : "Not connected. The user can run: cm-agent login --server <address>";
    }

    [McpServerTool(Name = "monitor_note")]
    [Description("Posts a short note to this session on Claude Monitor, where the user's team follows it. Use it for a milestone, a blocker or a question worth seeing from the web.")]
    public string Note(
        [Description("The session id (Claude Code's session_id).")] string sessionId,
        [Description("The note, at most 2000 characters.")] string text)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text)) return "Nothing posted: the session id and the text are required.";
        var note = text.Length > NoteMax ? text[..NoteMax] : text;
        using var store = new LocalStore(config.DatabasePath);
        new Recorder(config, store).Record(EventKinds.Note, sessionId, new JsonObject { ["text"] = note },
            ProjectInfo.Resolve(Environment.CurrentDirectory), clock.GetUtcNow());
        return "Posted.";
    }
}
