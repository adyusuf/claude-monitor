using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Podium.Agent.Capture;
using Podium.Agent.Config;
using Podium.Agent.Mcp;
using Podium.Agent.Push;
using Podium.Agent.Requests;
using Podium.Agent.Storage;
using Podium.Contracts;
using ModelContextProtocol.Server;

namespace Podium.Agent.Tests;

/// <summary>ADR-0016: every tool is called aipod_&lt;name&gt;, and every text that names a tool names it that way.</summary>
public sealed class McpToolNamesTests
{
    private static MethodInfo[] ToolMethods() => ToolRegistry.ToolTypes
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .ToArray();

    private static string[] Registered() => ToolMethods().Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!).Order().ToArray();

    [Fact]
    public void The_list_of_tool_names_is_exactly_what_is_registered()
    {
        Assert.Equal(Registered(), McpToolNames.All.Select(McpToolNames.Name).Order());
        Assert.Equal(19, Registered().Length);
    }

    [Fact]
    public void Every_registered_tool_is_named_aipod_and_a_name_appears_once()
    {
        var names = Registered();

        Assert.All(names, n => Assert.StartsWith("aipod_", n, StringComparison.Ordinal));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(names.Length, McpToolNames.All.Distinct().Count());
        Assert.Equal("aipod_", McpToolNames.Prefix);
    }

    [Fact]
    public void A_description_names_no_old_tool_and_every_tool_it_points_at_exists()
    {
        var known = McpToolNames.All.ToHashSet(StringComparer.Ordinal);

        foreach (var method in ToolMethods())
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), method.Name);
            Assert.DoesNotContain("monitor_", description, StringComparison.Ordinal);
            foreach (Match m in Regex.Matches(description!, @"\baipod_([a-z_]+)"))
                Assert.Contains(m.Groups[1].Value.TrimEnd('_'), known);
        }
    }

    [Fact]
    public void A_name_is_the_prefix_and_the_tool()
    {
        Assert.Equal("aipod_ask_result", McpToolNames.Name("ask_result"));
        Assert.Equal("aipod", McpToolNames.StoredWord);
    }

    [Fact]
    public void Only_the_known_slots_are_filled_and_other_braces_stay()
    {
        var kept = $"see {McpToolNames.Slot("machines")} and {{tool:nothing}} and {{x}} and {McpToolNames.Slot("run_result")}";

        Assert.Equal("{tool:machines}", McpToolNames.Slot("machines"));
        Assert.Equal("see aipod_machines and {tool:nothing} and {x} and aipod_run_result", McpToolNames.Fill(kept));
        Assert.Equal("aipod_ask_result and aipod_ask", McpToolNames.Fill("{tool:ask_result} and {tool:ask}")); // the longer name is not cut short
        Assert.Throws<ArgumentNullException>(() => McpToolNames.Fill(null!));
    }

    // ---- the texts the tools and the notices produce -------------------------------------------------------------

    [Fact]
    public async Task A_refusal_that_names_a_tool_names_the_aipod_tool()
    {
        var config = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? Path.GetTempPath() : null);
        var requests = new RemoteRequests(config, TimeProvider.System);

        var error = (await Targets.ResolveAsync(requests, " ")).Error!;

        Assert.Contains("aipod_machines", error, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor_", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_notices_written_into_a_session_name_the_tool_that_session_has()
    {
        var command = new LocalCommand("c-1", "s-1", CommandKinds.Prompt, "hello", DateTimeOffset.UtcNow.AddMinutes(5));
        var ask = new AskIn("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e01", "box", "which branch?", null, DateTimeOffset.UtcNow.AddMinutes(2), LocalStore.AskQueued, null, null);

        Assert.Contains("the aipod_inbox_read tool", ChannelEnvelopes.BuildInboxShort(command), StringComparison.Ordinal);
        var text = AskEnvelopes.Build(ask);
        Assert.Contains("the aipod_answer tool", text, StringComparison.Ordinal);
        Assert.Contains("which branch?", text, StringComparison.Ordinal); // the data is untouched
    }

    [Fact]
    public void The_question_text_from_another_machine_is_never_rewritten()
    {
        var ask = new AskIn("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e02", "box", "does {tool:status} work? psql aipod_prod", null, DateTimeOffset.UtcNow.AddMinutes(2), LocalStore.AskQueued, null, null);

        Assert.Contains("does {tool:status} work? psql aipod_prod", AskEnvelopes.Build(ask), StringComparison.Ordinal);
    }

    [Fact]
    public void The_machines_block_is_kept_with_slots_and_names_the_aipod_tools_once_filled()
    {
        var block = MachineContext.Rules + "\n" + McpToolNames.Slot("machines");

        Assert.Contains("{tool:machines}", block, StringComparison.Ordinal); // kept in the database with slots
        Assert.DoesNotContain("monitor_", block, StringComparison.Ordinal);
        Assert.Contains("aipod_machines, aipod_metrics, aipod_alerts", McpToolNames.Fill(block), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_start_hook_fills_the_slots_of_the_block()
    {
        using var home = new TempHome();
        using (var store = new LocalStore(home.Config.DatabasePath)) store.Set(AgentConfig.MachinesContextKey, "see " + McpToolNames.Slot("machines"));
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        await Cli.RunAsync(["hook", "SessionStart", "--context"], home.Config, new StringReader(""), stdout, stderr, TimeProvider.System);

        Assert.Equal("see aipod_machines", JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>());
    }
}
