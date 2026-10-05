using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Push;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>
/// The push into an idle session (ADR-0003), run only when push is on: the <see cref="PushLoop"/> asks the <see cref="PushPump"/> for
/// prompts the web sent this session and this writes each as a "notifications/claude/channel" message on the MCP connection.
/// Claude Code starts a turn from it.
/// </summary>
public sealed class ChannelHost(McpServer server, AgentConfig config, TimeProvider clock) : BackgroundService
{
    public const string Method = "notifications/claude/channel";
    public const string Capability = "claude/channel";

    protected override Task ExecuteAsync(CancellationToken stop) =>
        PushLoop.RunAsync(config, clock, () => server.ClientInfo is not null, Send, Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID"),
            message => Console.Error.WriteLine(message), stop); // stdout is the protocol's

    private Task Send(ChannelEnvelope message) =>
        server.SendNotificationAsync(Method, new JsonObject
        {
            ["content"] = message.Content,
            ["meta"] = new JsonObject(message.Meta.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key, m.Value))),
        });
}
