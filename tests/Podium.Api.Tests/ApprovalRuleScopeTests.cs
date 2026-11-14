using System.Text.Json;
using Podium.Api.Approvals;
using Podium.Api.Data;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>What a permission request can be remembered as (ADR-0019): the tool allowlist, the one value a rule pins, the guarded paths and the strict-machine test.</summary>
public sealed class ApprovalRuleScopeTests
{
    private static (string Tool, string? Command)? Scope(string tool, string json)
    {
        using var document = JsonDocument.Parse(json);
        return ApprovalRules.ScopeOf(tool, document.RootElement.Clone());
    }

    [Theory]
    [InlineData("Bash", """{"command":"git status"}""", "git status")]
    [InlineData("Bash", """{"command":"git status","description":"state","timeout":5000}""", "git status")]
    [InlineData("Bash", """{"command":"git status","run_in_background":false}""", "git status")]
    [InlineData("Read", """{"file_path":"/repo/src/a.cs","offset":3}""", "/repo/src/a.cs")]
    [InlineData("LS", """{"path":"/repo/src"}""", "/repo/src")]
    [InlineData("Edit", """{"file_path":"/repo/src/a.cs","old_string":"x","new_string":"y"}""", "/repo/src/a.cs")]
    [InlineData("Write", """{"file_path":"/repo/notes.txt","content":"x"}""", "/repo/notes.txt")]
    [InlineData("MultiEdit", """{"file_path":"/repo/src/a.cs","edits":[]}""", "/repo/src/a.cs")]
    [InlineData("NotebookEdit", """{"notebook_path":"/repo/n.ipynb","new_source":"x"}""", "/repo/n.ipynb")]
    [InlineData("Write", """{"file_path":"C:\\repo\\notes.txt"}""", "C:\\repo\\notes.txt")]
    public void A_tool_in_the_allowlist_pins_its_one_value(string tool, string input, string expected) =>
        Assert.Equal((tool, expected), Scope(tool, input));

    [Theory]
    [InlineData("Glob", """{"pattern":"*.cs","path":"/repo"}""", """{"path":"/repo","pattern":"*.cs"}""")]
    [InlineData("Glob", """{"path":"/repo","pattern":"*.cs"}""", """{"path":"/repo","pattern":"*.cs"}""")]
    [InlineData("Grep", """{"pattern":"todo","head_limit":5,"multiline":false,"path":null}""", """{"head_limit":5,"multiline":false,"path":null,"pattern":"todo"}""")]
    public void A_search_pins_every_key_of_the_call_in_one_canonical_text_whatever_order_it_came_in(string tool, string input, string expected) =>
        Assert.Equal((tool, expected), Scope(tool, input));

    [Theory]
    [InlineData("WebSearch")]
    [InlineData("WebFetch")]
    [InlineData("mcp__server__tool")]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("SomethingNew")]
    [InlineData("bash")]
    [InlineData("")]
    public void A_tool_outside_the_allowlist_gets_no_rule(string tool) => Assert.Null(Scope(tool, """{"command":"ls","file_path":"/repo/a.txt"}"""));

    [Fact]
    public void A_call_whose_input_is_not_an_object_gets_no_rule()
    {
        Assert.Null(Scope("Bash", """["ls"]"""));
        Assert.Null(Scope("Read", "\"/repo/a.txt\""));
    }

    [Theory]
    [InlineData("""{"command":"ls","run_in_background":true}""")]
    [InlineData("""{"command":"ls","dangerouslyDisableSandbox":true}""")]
    [InlineData("""{"command":"ls","unknown_flag":"x"}""")]
    public void A_shell_call_with_a_flag_that_changes_how_it_runs_is_one_off(string input) => Assert.Null(Scope("Bash", input));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"command":""}""")]
    [InlineData("""{"command":"   "}""")]
    [InlineData("""{"command":5}""")]
    [InlineData("""{"command":"echo [masked:secret]"}""")]
    [InlineData("""{"command":"ls\u202e -a"}""")]
    [InlineData("""{"command":"ls\u200b"}""")]
    public void A_shell_command_that_is_missing_empty_masked_or_has_hidden_characters_gets_no_rule(string input) => Assert.Null(Scope("Bash", input));

    [Fact]
    public void A_shell_command_longer_than_the_limit_gets_no_rule_and_one_of_exactly_the_limit_does()
    {
        var atLimit = new string('a', ApprovalRuleLimits.CommandMax);
        var over = new string('a', ApprovalRuleLimits.CommandMax + 1);

        Assert.Equal(("Bash", atLimit), Scope("Bash", JsonSerializer.Serialize(new { command = atLimit })));
        Assert.Null(Scope("Bash", JsonSerializer.Serialize(new { command = over })));
    }

    [Theory]
    [InlineData("/repo/.git/config")]
    [InlineData("/repo/.GIT/config")]
    [InlineData("/repo/.claude/settings.json")]
    [InlineData("/repo/.github/workflows/ci.yml")]
    [InlineData("/home/me/.ssh/authorized_keys")]
    [InlineData("C:\\Users\\me\\.ssh\\config")]
    [InlineData("/Users/me/Library/LaunchAgents/x.plist")]
    [InlineData("/repo/src/../secret.txt")]
    [InlineData("/repo/notes.")]
    [InlineData("/repo/notes ")]
    [InlineData("/repo/notes.txt:stream")]
    [InlineData("/repo/.env")]
    [InlineData("/repo/.env.local")]
    [InlineData("/repo/run.sh")]
    [InlineData("/repo/build.PS1")]
    [InlineData("/repo/App.csproj")]
    [InlineData("/repo/Directory.Build.props")]
    [InlineData("/repo/package.json")]
    [InlineData("/repo/MAKEFILE")]
    [InlineData("/home/me/.bashrc")]
    [InlineData("/home/me/.zshrc")]
    [InlineData("/repo/conftest.py")]
    [InlineData("/repo/settings.json")]
    public void A_file_tool_never_pins_a_path_that_configures_a_shell_git_or_the_build(string path) =>
        Assert.Null(Scope("Write", JsonSerializer.Serialize(new { file_path = path })));

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("MultiEdit")]
    public void Every_file_tool_refuses_a_guarded_path(string tool) =>
        Assert.Null(Scope(tool, JsonSerializer.Serialize(new { file_path = "/repo/.git/config" })));

    [Fact]
    public void A_notebook_edit_on_a_guarded_path_gets_no_rule() =>
        Assert.Null(Scope("NotebookEdit", JsonSerializer.Serialize(new { notebook_path = "/repo/.git/x.ipynb" })));

    [Theory]
    [InlineData("Edit", """{"file_path":"/repo/a.txt","notebook_path":"/repo/.git/hooks/x"}""")]
    [InlineData("NotebookEdit", """{"notebook_path":"/repo/n.ipynb","file_path":"/repo/.git/hooks/x"}""")]
    [InlineData("Edit", """{"notebook_path":"/repo/a.txt"}""")]
    [InlineData("Write", """{"content":"x"}""")]
    [InlineData("Write", """{"file_path":"/repo/a\u200b.txt"}""")]
    [InlineData("Write", """{"file_path":"/repo/[masked:token]/a.txt"}""")]
    public void A_file_call_that_names_a_second_path_or_none_or_a_hidden_one_gets_no_rule(string tool, string input) => Assert.Null(Scope(tool, input));

    [Theory]
    [InlineData("""{"pattern":"a","options":{"deep":true}}""")]
    [InlineData("""{"pattern":"a","paths":["/x"]}""")]
    [InlineData("""{"pattern":"[masked:key]"}""")]
    [InlineData("""{"pattern":"a\u200bb"}""")]
    public void A_search_with_a_nested_masked_or_hidden_value_gets_no_rule(string input)
    {
        Assert.Null(Scope("Grep", input));
        Assert.Null(Scope("Glob", input));
    }

    [Fact]
    public void A_search_whose_canonical_text_is_longer_than_the_limit_gets_no_rule()
    {
        var tooLong = JsonSerializer.Serialize(new { pattern = new string('a', ApprovalRuleLimits.CommandMax) });

        Assert.Null(Scope("Grep", tooLong));
    }

    [Theory]
    [InlineData("Read", """{"file_path":"/repo/a.txt"}""", true, "/repo/a.txt")]
    [InlineData("LS", """{"path":"/repo"}""", true, "/repo")]
    [InlineData("Edit", """{"file_path":"/repo/a.txt"}""", true, "/repo/a.txt")]
    [InlineData("NotebookEdit", """{"notebook_path":"/repo/n.ipynb"}""", true, "/repo/n.ipynb")]
    [InlineData("Glob", """{"pattern":"*","path":"/repo"}""", true, "/repo")]
    [InlineData("Grep", """{"pattern":"x","path":"/repo"}""", true, "/repo")]
    [InlineData("Read", """{}""", true, null)]
    [InlineData("Edit", """{"file_path":5}""", true, null)]
    [InlineData("Read", """{"file_path":"/repo/a\u200b.txt"}""", true, null)]
    [InlineData("Glob", """{"pattern":"*","path":5}""", true, null)]
    [InlineData("Glob", """{"pattern":"*"}""", false, null)]
    [InlineData("Grep", """{"pattern":"x"}""", false, null)]
    [InlineData("Bash", """{"command":"cat /repo/a.txt"}""", false, null)]
    [InlineData("WebFetch", """{"url":"x"}""", false, null)]
    public void A_path_call_is_a_file_tool_or_a_search_that_carries_a_path_and_its_path_is_read_from_its_own_key(string tool, string input, bool isPathCall, string? path)
    {
        using var document = JsonDocument.Parse(input);

        var result = ApprovalRules.IsPathCall(tool, document.RootElement, out var found);

        Assert.Equal((isPathCall, path), (result, found));
    }

    [Theory]
    [InlineData("/a/b", true)]
    [InlineData("/x", true)]
    [InlineData("rel/path", true)]
    [InlineData("C:\\a\\b", true)]
    [InlineData("/a/./b", false)]
    [InlineData("/a/../b", false)]
    [InlineData("a\\..\\b", false)]
    [InlineData("/a//b", false)]
    [InlineData("//a", false)]
    [InlineData("/a/b/", false)]
    [InlineData("/", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    public void A_path_is_plain_when_no_segment_is_dot_dotdot_or_empty(string path, bool expected) =>
        Assert.Equal(expected, ApprovalRuleAccess.PlainSegments(path));

    [Theory]
    [InlineData("production", null, null, null, true)]
    [InlineData(null, "production", null, null, true)]
    [InlineData("test", "production", null, null, true)]
    [InlineData("test", null, "database", null, true)]
    [InlineData("test", null, null, "database", true)]
    [InlineData("production", null, "application", null, true)]
    [InlineData("test", null, "application", null, false)]
    [InlineData("dev", "other", "development", "web", false)]
    [InlineData(null, null, null, null, false)]
    public void A_machine_is_strict_when_its_set_role_or_its_own_claim_says_production_or_database(string? roleEnvironment, string? claimedEnvironment,
        string? roleFunction, string? claimedFunction, bool strict) =>
        Assert.Equal(strict, ApprovalRules.Strict(new Agent
        {
            RoleEnvironment = roleEnvironment,
            ClaimedEnvironment = claimedEnvironment,
            RoleFunction = roleFunction,
            ClaimedFunction = claimedFunction,
        }));
}
