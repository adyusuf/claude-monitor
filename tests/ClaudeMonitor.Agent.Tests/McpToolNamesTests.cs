using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Mcp;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Requests;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;
using ModelContextProtocol.Server;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>ADR-0013 step 3: the plugin picks the names of the tools (old by default), and every text that names a tool names the ones the session has.</summary>
public sealed class McpToolNamesTests
{
    private static string[] Registered() => ToolRegistry.ToolTypes
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>().Order().ToArray();

    [Fact]
    public void The_list_of_tool_names_is_exactly_what_is_registered()
    {
        Assert.Equal(Registered(), McpToolNames.All.Select(n => McpToolNames.LegacyPrefix + n).Order());
        Assert.Equal(19, Registered().Length);
    }

    [Theory]
    [InlineData("aipod", ToolNameMode.Aipod)]
    [InlineData(" AIPOD ", ToolNameMode.Aipod)]
    [InlineData("legacy", ToolNameMode.Legacy)]
    [InlineData("", ToolNameMode.Legacy)]
    [InlineData(null, ToolNameMode.Legacy)]
    [InlineData("monitor", ToolNameMode.Legacy)]
    [InlineData("anything else", ToolNameMode.Legacy)]
    public void Only_the_word_aipod_selects_the_new_names_and_anything_else_is_the_safe_old_default(string? text, ToolNameMode mode) =>
        Assert.Equal(mode, McpToolNames.Parse(text));

    [Fact]
    public void A_name_is_the_prefix_of_the_mode_and_the_word_round_trips()
    {
        Assert.Equal("monitor_ask_result", McpToolNames.Name(ToolNameMode.Legacy, "ask_result"));
        Assert.Equal("aipod_ask_result", McpToolNames.Name(ToolNameMode.Aipod, "ask_result"));
        Assert.Equal("aipod", McpToolNames.Word(ToolNameMode.Aipod));
        Assert.Equal("legacy", McpToolNames.Word(ToolNameMode.Legacy));
        Assert.Equal(ToolNameMode.Aipod, McpToolNames.Parse(McpToolNames.Word(ToolNameMode.Aipod)));
        Assert.Equal(ToolNameMode.Legacy, McpToolNames.Mode); // nothing said: the old names
    }

    [Fact]
    public void A_scope_changes_the_names_for_its_own_call_only_and_puts_them_back()
    {
        using (McpToolNames.Scope(ToolNameMode.Aipod))
        {
            Assert.Equal("aipod_status", McpToolNames.Name("status"));
            using (McpToolNames.Scope(ToolNameMode.Legacy)) Assert.Equal("monitor_status", McpToolNames.Name("status"));
            Assert.Equal("aipod_status", McpToolNames.Name("status"));
        }

        Assert.Equal("monitor_status", McpToolNames.Name("status"));
    }

    [Fact]
    public async Task A_scope_does_not_leak_into_a_parallel_test()
    {
        var other = Task.Run(async () =>
        {
            await Task.Delay(50);
            return McpToolNames.Mode;
        });
        using (McpToolNames.Scope(ToolNameMode.Aipod)) Assert.Equal(ToolNameMode.Aipod, McpToolNames.Mode);
        Assert.Equal(ToolNameMode.Legacy, await other);
    }

    [Fact]
    public void Only_the_known_tool_names_are_rewritten_whole_and_everything_else_is_left_alone()
    {
        const string text = "Call monitor_ask_result then monitor_ask; see monitor_machine and monitor_machines. A database monitor_prod, monitor_status_page, xmonitor_status and monitor_ are data.";

        var aipod = McpToolNames.Text(ToolNameMode.Aipod, text);

        Assert.Equal("Call aipod_ask_result then aipod_ask; see aipod_machine and aipod_machines. A database monitor_prod, monitor_status_page, xmonitor_status and monitor_ are data.", aipod);
        Assert.Equal(text, McpToolNames.Text(ToolNameMode.Legacy, text));
        Assert.Throws<ArgumentNullException>(() => McpToolNames.Text(ToolNameMode.Aipod, null!));
    }

    [Fact]
    public void A_slot_is_filled_in_the_readers_mode_and_other_braces_stay()
    {
        var kept = $"see {McpToolNames.Slot("machines")} and {{tool:nothing}} and {{x}} and {McpToolNames.Slot("run_result")}";

        Assert.Equal("see monitor_machines and {tool:nothing} and {x} and monitor_run_result", McpToolNames.Fill(ToolNameMode.Legacy, kept));
        Assert.Equal("see aipod_machines and {tool:nothing} and {x} and aipod_run_result", McpToolNames.Fill(ToolNameMode.Aipod, kept));
        using (McpToolNames.Scope(ToolNameMode.Aipod)) Assert.Contains("aipod_machines", McpToolNames.Fill(kept), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => McpToolNames.Fill(ToolNameMode.Legacy, null!));
    }

    // ---- the registered tools ------------------------------------------------------------------------------------

    private static IReadOnlyList<McpServerTool> Tools(ToolNameMode mode)
    {
        var config = AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? Path.GetTempPath() : null);
        return ToolRegistry.Create(config, TimeProvider.System, mode);
    }

    [Fact]
    public void The_new_names_list_every_tool_once_with_no_old_name_left_in_a_description_or_a_parameter()
    {
        var tools = Tools(ToolNameMode.Aipod);

        Assert.Equal(19, tools.Count);
        Assert.Equal(Registered().Select(n => "aipod_" + n[8..]).Order(), tools.Select(t => t.ProtocolTool.Name).Order());
        Assert.All(tools, t =>
        {
            var json = JsonSerializer.Serialize(t.ProtocolTool);
            Assert.DoesNotMatch(@"\bmonitor_(" + string.Join('|', McpToolNames.All) + @")\b", json);
            Assert.False(string.IsNullOrWhiteSpace(t.ProtocolTool.Description));
        });
        Assert.Contains(tools, t => t.ProtocolTool.Description!.Contains("aipod_ask_result", StringComparison.Ordinal)); // a description that points at another tool points at the new name
    }

    [Fact]
    public void The_two_sets_are_the_same_tools_apart_from_their_names_and_the_names_inside_their_texts()
    {
        var old = Tools(ToolNameMode.Legacy).ToDictionary(t => t.ProtocolTool.Name[8..]);
        var renamed = Tools(ToolNameMode.Aipod).ToDictionary(t => t.ProtocolTool.Name[6..]);

        Assert.Equal(old.Keys.Order(), renamed.Keys.Order());
        foreach (var (key, tool) in renamed)
        {
            var before = old[key].ProtocolTool;
            var after = tool.ProtocolTool;
            Assert.Equal(before.Annotations?.ReadOnlyHint, after.Annotations?.ReadOnlyHint);
            Assert.Equal(before.Annotations?.DestructiveHint, after.Annotations?.DestructiveHint);
            Assert.Equal(before.Annotations?.IdempotentHint, after.Annotations?.IdempotentHint);
            Assert.Equal(before.Annotations?.OpenWorldHint, after.Annotations?.OpenWorldHint);
            Assert.Equal(McpToolNames.Text(ToolNameMode.Aipod, before.Description!), after.Description);
            Assert.Equal(McpToolNames.Text(ToolNameMode.Aipod, before.InputSchema.GetRawText()), after.InputSchema.GetRawText());
        }
    }

    // ---- the texts the tools and the notices produce -------------------------------------------------------------

    [Fact]
    public async Task A_refusal_that_names_a_tool_names_the_tool_of_the_session()
    {
        var config = AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? Path.GetTempPath() : null);
        var requests = new RemoteRequests(config, TimeProvider.System);

        Assert.Contains("monitor_machines", (await Targets.ResolveAsync(requests, " ")).Error, StringComparison.Ordinal);
        using (McpToolNames.Scope(ToolNameMode.Aipod))
        {
            var aipod = (await Targets.ResolveAsync(requests, " ")).Error!;
            Assert.Contains("aipod_machines", aipod, StringComparison.Ordinal);
            Assert.DoesNotContain("monitor_", aipod, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_notices_written_into_a_session_name_the_tool_that_session_has()
    {
        var command = new LocalCommand("c-1", "s-1", CommandKinds.Prompt, "hello", DateTimeOffset.UtcNow.AddMinutes(5));
        var ask = new AskIn("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e01", "box", "which branch?", null, DateTimeOffset.UtcNow.AddMinutes(2), LocalStore.AskQueued, null, null);

        Assert.Contains("the monitor_inbox_read tool", ChannelEnvelopes.BuildInboxShort(command), StringComparison.Ordinal);
        Assert.Contains("the aipod_inbox_read tool", ChannelEnvelopes.BuildInboxShort(command, ToolNameMode.Aipod), StringComparison.Ordinal);
        Assert.Contains("the monitor_answer tool", AskEnvelopes.Build(ask), StringComparison.Ordinal);
        var aipod = AskEnvelopes.Build(ask, mode: ToolNameMode.Aipod);
        Assert.Contains("the aipod_answer tool", aipod, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor_answer", aipod, StringComparison.Ordinal);
        Assert.Contains("which branch?", aipod, StringComparison.Ordinal); // the data is untouched
    }

    [Fact]
    public void The_question_text_from_another_machine_is_never_rewritten()
    {
        var ask = new AskIn("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e02", "box", "does monitor_status work? psql monitor_prod", null, DateTimeOffset.UtcNow.AddMinutes(2), LocalStore.AskQueued, null, null);
        var text = AskEnvelopes.Build(ask, mode: ToolNameMode.Aipod);
        Assert.Contains("does monitor_status work? psql monitor_prod", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_machines_block_names_the_tools_of_the_session_that_reads_it_and_keeps_its_cap()
    {
        var block = MachineContext.Rules + "\n" + McpToolNames.Slot("machines");
        Assert.Contains("{tool:machines}", block, StringComparison.Ordinal); // kept in the database with slots
        Assert.DoesNotContain("monitor_", block, StringComparison.Ordinal);
        Assert.Contains("monitor_machines, monitor_metrics, monitor_alerts", McpToolNames.Fill(ToolNameMode.Legacy, block), StringComparison.Ordinal);
        Assert.Contains("aipod_machines, aipod_metrics, aipod_alerts", McpToolNames.Fill(ToolNameMode.Aipod, block), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_start_hook_fills_the_block_for_the_mode_it_was_started_in()
    {
        using var home = new TempHome();
        using (var store = new LocalStore(home.Config.DatabasePath)) store.Set(AgentConfig.MachinesContextKey, "see " + McpToolNames.Slot("machines"));

        async Task<string> Run(params string[] extra)
        {
            var (stdout, stderr) = (new StringWriter(), new StringWriter());
            await Cli.RunAsync(["hook", "SessionStart", "--context", .. extra], home.Config, new StringReader(""), stdout, stderr, TimeProvider.System);
            return JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        }

        Assert.Equal("see monitor_machines", await Run());
        Assert.Equal("see aipod_machines", await Run("--names", "aipod"));
        Assert.Equal("see monitor_machines", await Run("--names", "legacy"));
        var viaEnvironment = home.Config with { ToolNamesMode = ToolNameMode.Aipod }; // AIPOD_TOOL_NAMES=aipod, as the new plugin's wrapper sets it
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        await Cli.RunAsync(["hook", "SessionStart", "--context"], viaEnvironment, new StringReader(""), stdout, stderr, TimeProvider.System);
        Assert.Contains("see aipod_machines", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(ToolNameMode.Aipod, AgentConfig.FromEnvironment(k => k == "AIPOD_TOOL_NAMES" ? "aipod" : null).ToolNamesMode);
        Assert.Equal(ToolNameMode.Aipod, AgentConfig.FromEnvironment(k => k == "CM_TOOL_NAMES" ? "aipod" : null).ToolNamesMode);
        Assert.Equal(ToolNameMode.Legacy, AgentConfig.FromEnvironment(_ => null).ToolNamesMode);
    }
}
