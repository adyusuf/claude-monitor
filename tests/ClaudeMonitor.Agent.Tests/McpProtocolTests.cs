using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

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
        foreach (var arg in extraArgs ?? []) info.ArgumentList.Add(arg);
        if (environment is { } extra) info.Environment[extra.Item1] = extra.Item2;
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
            Assert.Equal(["monitor_alerts", "monitor_answer", "monitor_ask", "monitor_ask_result", "monitor_grants", "monitor_inbox_read", "monitor_job_propose", "monitor_job_run", "monitor_jobs",
                "monitor_machine", "monitor_machines", "monitor_metrics", "monitor_note", "monitor_request_grant", "monitor_run", "monitor_run_cancel", "monitor_run_result",
                "monitor_run_script", "monitor_status"], names);

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

    private static async Task<(List<string> Names, JsonNode Old, JsonNode New)> Probe(Process p)
    {
        await CallAsync(p, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var list = await CallAsync(p, 2, "tools/list", new { });
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToList();
        var old = await CallAsync(p, 3, "tools/call", new { name = "monitor_status", arguments = new { } });
        var renamed = await CallAsync(p, 4, "tools/call", new { name = "aipod_status", arguments = new { } });
        return (names, old, renamed);
    }

    [Theory]
    [InlineData("flag")]
    [InlineData("environment")]
    public async Task With_the_new_names_the_server_lists_aipod_tools_only_and_answers_them_and_not_the_old_ones(string how)
    {
        using var p = how == "flag" ? Start(["--names", "aipod"]) : Start(environment: ("AIPOD_TOOL_NAMES", "aipod"));
        try
        {
            var (names, old, renamed) = await Probe(p);

            Assert.Equal(19, names.Count);
            Assert.All(names, n => Assert.StartsWith("aipod_", n, StringComparison.Ordinal));
            Assert.Contains("aipod_status", names);
            Assert.StartsWith("Not connected", renamed["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(old["error"] is not null || old["result"]!["isError"]?.GetValue<bool>() == true, "the old name must not be a tool of this server");
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task With_no_names_given_or_the_word_legacy_the_old_names_are_served_exactly_as_before()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["--names", "legacy"] })
        {
            using var p = Start(args);
            try
            {
                var (names, old, renamed) = await Probe(p);

                Assert.Equal(19, names.Count);
                Assert.All(names, n => Assert.StartsWith("monitor_", n, StringComparison.Ordinal));
                Assert.StartsWith("Not connected", old["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.True(renamed["error"] is not null || renamed["result"]!["isError"]?.GetValue<bool>() == true);
            }
            finally
            {
                p.Kill(entireProcessTree: true);
            }
        }
    }
}
