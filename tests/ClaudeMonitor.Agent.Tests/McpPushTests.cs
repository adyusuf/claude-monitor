using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// "cm-agent mcp" as a channel (ADR-0003), over its real transport: a child process speaking JSON-RPC on stdio, the way Claude
/// Code starts it. Push on: it declares claude/channel and writes a notification when a web prompt lands in the local database
/// while it is idle. Push off: it is the server it always was.
/// </summary>
public sealed class McpPushTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private Process Start(string? push, string? session)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "cm-agent.dll"));
        info.ArgumentList.Add("mcp");
        info.Environment["CM_AGENT_HOME"] = home.Dir;
        info.Environment["CM_CREDENTIALS"] = "file";
        info.Environment.Remove("CM_PUSH");
        if (push is not null) info.Environment["CM_PUSH"] = push;
        info.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        if (session is not null) info.Environment["CLAUDE_CODE_SESSION_ID"] = session;
        return Process.Start(info)!;
    }

    private static async Task<JsonNode> ReadAsync(Process p, Func<JsonNode, bool> match, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        while (await p.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            var message = JsonNode.Parse(line)!;
            if (match(message)) return message;
        }

        throw new InvalidOperationException("the MCP server closed its output");
    }

    private static async Task<JsonNode> InitializeAsync(Process p)
    {
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
        }));
        var init = await ReadAsync(p, m => m["id"]?.GetValue<int>() == 1, TimeSpan.FromSeconds(30));
        await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        return init;
    }

    [Fact]
    public async Task With_push_off_the_server_declares_no_channel_and_says_nothing_on_its_own()
    {
        using var p = Start(null, "s-off");
        try
        {
            var init = await InitializeAsync(p);
            Assert.Null(init["result"]!["capabilities"]!["experimental"]?["claude/channel"]);
            Assert.Null(init["result"]!["instructions"]);
            using var store = new LocalStore(home.Config.DatabasePath);
            store.SaveCommand(new LocalCommand("c-1", "s-off", CommandKinds.Prompt, "hello", DateTimeOffset.UtcNow.AddMinutes(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ReadAsync(p, m => m["method"]?.GetValue<string>() == "notifications/claude/channel", TimeSpan.FromSeconds(2)));
            Assert.Equal("c-1", store.TakeCommand("s-off", CommandKinds.Prompt, DateTimeOffset.UtcNow)!.Id); // still there for the hook
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task With_push_on_an_idle_server_writes_the_web_message_as_a_channel_notification_once()
    {
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);
        using var p = Start("on", "s-on");
        try
        {
            var init = await InitializeAsync(p);
            Assert.NotNull(init["result"]!["capabilities"]!["experimental"]!["claude/channel"]);
            Assert.Contains("DATA", init["result"]!["instructions"]!.GetValue<string>(), StringComparison.Ordinal);

            using var store = new LocalStore(home.Config.DatabasePath);
            store.SaveCommand(new LocalCommand("c-1", "s-on", CommandKinds.Prompt, "please run the tests", DateTimeOffset.UtcNow.AddMinutes(5)));
            store.SaveCommand(new LocalCommand("c-2", "s-other", CommandKinds.Prompt, "not for this session", DateTimeOffset.UtcNow.AddMinutes(5)));
            var sw = Stopwatch.StartNew();
            var n = await ReadAsync(p, m => m["method"]?.GetValue<string>() == "notifications/claude/channel", TimeSpan.FromSeconds(10));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"arrived after {sw.Elapsed}");
            Assert.Equal("<<<claude-monitor-message id=c-1\nplease run the tests\nclaude-monitor-message>>>", n["params"]!["content"]!.GetValue<string>());
            Assert.Equal(("c-1", "s-on", "web"), (n["params"]!["meta"]!["message_id"]!.GetValue<string>(), n["params"]!["meta"]!["session_id"]!.GetValue<string>(),
                n["params"]!["meta"]!["origin"]!.GetValue<string>()));

            var status = await CallStatusAsync(p);
            Assert.StartsWith("Connected to https://m.invalid", status, StringComparison.Ordinal);
            Assert.Contains("Push into the session: on for this session (s-on); last pushed c-1", status, StringComparison.Ordinal);
            Assert.Contains("1 pushed but not yet seen in the transcript", status, StringComparison.Ordinal);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => // once: no second notification, none for the other session
                ReadAsync(p, m => m["method"]?.GetValue<string>() == "notifications/claude/channel", TimeSpan.FromSeconds(2)));
            Assert.Equal("c-2", store.TakeCommand("s-other", CommandKinds.Prompt, DateTimeOffset.UtcNow)!.Id);
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string> CallStatusAsync(Process p)
    {
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 9, method = "tools/call", @params = new { name = "monitor_status", arguments = new { } } }));
        var reply = await ReadAsync(p, m => m["id"]?.GetValue<int>() == 9, TimeSpan.FromSeconds(30));
        return reply["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }
}
