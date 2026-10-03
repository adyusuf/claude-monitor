using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// "cm-agent mcp" over its real transport: a child process speaking JSON-RPC on stdio, as Claude Code starts it.
/// The agent home is a throw-away folder with file tokens; nothing of the user's is touched.
/// </summary>
public sealed class McpProtocolTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private Process Start()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "cm-agent.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("mcp");
        info.Environment["CM_AGENT_HOME"] = home.Dir;
        info.Environment["CM_CREDENTIALS"] = "file";
        return Process.Start(info)!;
    }

    private static async Task<JsonNode> CallAsync(Process p, int id, string method, object parameters)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await p.StandardInput.WriteLineAsync(request);
        await p.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await p.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var message = JsonNode.Parse(line)!;
            if (message["id"]?.GetValue<int>() == id) return message;
        }

        throw new InvalidOperationException("the MCP server closed its output");
    }

    [Fact]
    public async Task The_server_initialises_lists_its_tools_and_answers_a_call()
    {
        using var p = Start();
        try
        {
            var init = await CallAsync(p, 1, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "test", version = "1" },
            });
            Assert.False(string.IsNullOrEmpty(init["result"]!["serverInfo"]!["name"]!.GetValue<string>()));
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var list = await CallAsync(p, 2, "tools/list", new { });
            var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToList();
            Assert.Equal(["monitor_note", "monitor_status"], names);

            var status = await CallAsync(p, 3, "tools/call", new { name = "monitor_status", arguments = new { } });
            Assert.StartsWith("Not connected", status["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

            var note = await CallAsync(p, 4, "tools/call", new { name = "monitor_note", arguments = new { sessionId = "s-mcp", text = "milestone reached" } });
            Assert.Equal("Posted.", note["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }

        using var store = new LocalStore(home.Config.DatabasePath);
        var e = Assert.Single(store.NextBatch(10, int.MaxValue)!.Value.Rows).Event;
        Assert.Equal((EventKinds.Note, "s-mcp"), (e.Kind, e.SessionExternalId));
        Assert.Equal("milestone reached", e.Payload.GetProperty("text").GetString());
    }
}
