using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

// The question tool's answers (the same test class, in its own file so neither passes 300 lines).
public sealed partial class HookRunnerTests
{
    [Fact]
    public async Task A_chosen_option_becomes_the_questions_answered_input_built_from_the_tools_own_input()
    {
        LogIn(fresh: true);
        using var daemon = new LocalStore(home.Config.DatabasePath);
        var questions = new[] { new { question = "Which way?", header = "Way", multiSelect = false, options = new[] { new { label = "Left" }, new { label = "Right" } } } };
        var running = Run("PermissionRequest", new { session_id = "s1", tool_name = "AskUserQuestion", tool_input = new { questions } });
        var asked = await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        daemon.PermissionSent(asked.LocalId, "q1");
        daemon.PermissionAnswered("q1", PermissionDecisions.Allow, null, "{\"Which way?\":\"Right\"}");
        var decision = JsonNode.Parse((await running)!)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", decision["behavior"]!.GetValue<string>());
        Assert.Equal("Right", decision["updatedInput"]!["answers"]!["Which way?"]!.GetValue<string>());
        Assert.Equal("Left", decision["updatedInput"]!["questions"]![0]!["options"]![0]!["label"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("AskUserQuestion", null)]
    [InlineData("Bash", "{\"x\":\"y\"}")]
    public async Task Answers_change_nothing_unless_the_call_is_the_question_tool_and_something_was_chosen(string tool, string? answers)
    {
        LogIn(fresh: true);
        using var daemon = new LocalStore(home.Config.DatabasePath);
        var running = Run("PermissionRequest", new { session_id = "s1", tool_name = tool, tool_input = new { command = "ls" } });
        var asked = await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        daemon.PermissionSent(asked.LocalId, "q2");
        daemon.PermissionAnswered("q2", PermissionDecisions.Allow, null, answers);
        var decision = JsonNode.Parse((await running)!)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", decision["behavior"]!.GetValue<string>());
        Assert.Null(decision["updatedInput"]);
    }
}
