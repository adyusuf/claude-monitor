using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// "aipod mcp" over its real transport: a child process speaking JSON-RPC on stdio, as Claude Code starts it.
/// The agent home is a throw-away folder with file tokens; nothing of the user's is touched.
/// </summary>
public sealed class McpProtocolTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private Process Start(string[]? extraArgs = null, (string, string)? environment = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "aipod.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("mcp");
        foreach (var arg in extraArgs ?? []) info.ArgumentList.Add(arg);
        if (environment is { } extra) info.Environment[extra.Item1] = extra.Item2;
        info.Environment["AP_AGENT_HOME"] = home.Dir;
        info.Environment["AP_CREDENTIALS"] = "file";
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
            Assert.Equal(["aipod_alerts", "aipod_answer", "aipod_ask", "aipod_ask_result", "aipod_grants", "aipod_inbox_read", "aipod_job_propose", "aipod_job_run", "aipod_jobs",
                "aipod_machine", "aipod_machines", "aipod_metrics", "aipod_note", "aipod_request_grant", "aipod_run", "aipod_run_cancel", "aipod_run_result",
                "aipod_run_script", "aipod_status"], names);

            var status = await CallAsync(p, 3, "tools/call", new { name = "aipod_status", arguments = new { } });
            Assert.StartsWith("Not connected", status["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

            var note = await CallAsync(p, 4, "tools/call", new { name = "aipod_note", arguments = new { sessionId = "s-mcp", text = "milestone reached" } });
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

    [Fact]
    public async Task The_server_serves_the_aipod_names_only_and_does_not_know_the_names_it_had_before_the_rename()
    {
        using var p = Start();
        try
        {
            await CallAsync(p, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            var list = await CallAsync(p, 2, "tools/list", new { });
            var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
            var old = await CallAsync(p, 3, "tools/call", new { name = "monitor_status", arguments = new { } });

            Assert.Equal(19, names.Count);
            Assert.All(names, n => Assert.StartsWith("aipod_", n, StringComparison.Ordinal));
            Assert.True(old["error"] is not null || old["result"]!["isError"]?.GetValue<bool>() == true, "the old name must not be a tool of this server");
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }
}
