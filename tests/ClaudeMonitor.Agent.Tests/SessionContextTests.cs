using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>"cm-agent hook SessionStart --context" (ADR-0008 §3b): the block from the local database as additionalContext, and silence on every other path, always exit 0.</summary>
public sealed class SessionContextTests : IDisposable
{
    private static readonly string[] Args = ["hook", "SessionStart", "--context"];

    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private void Keep(string? text)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        if (text is not null) store.Set(AgentConfig.MachinesContextKey, text);
    }

    private async Task<(int Code, string Out, string Err)> Run(AgentConfig? config = null)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(Args, config ?? home.Config, new StringReader(""), stdout, stderr, TimeProvider.System);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task The_stored_block_is_printed_as_the_additional_context_of_a_SessionStart_hook()
    {
        const string text = "Machines of this workspace\n- \"quoted\" host-é <<<x>>>";
        Keep(text);

        var (code, output, error) = await Run();

        Assert.Equal(0, code);
        Assert.Equal("", error);
        var specific = JsonNode.Parse(output)!["hookSpecificOutput"]!;
        Assert.Equal("SessionStart", (string?)specific["hookEventName"]);
        Assert.Equal(text, (string?)specific["additionalContext"]);
        Assert.Equal(2, ((JsonObject)specific).Count);
        Assert.Single((JsonObject)JsonNode.Parse(output)!);
    }

    [Fact]
    public async Task The_hook_does_not_start_a_daemon_and_does_not_need_a_server()
    {
        Keep("block");

        var (code, _, _) = await Run();

        Assert.Equal(0, code);
        Assert.False(File.Exists(home.Config.PidPath));
        Assert.False(File.Exists(home.Config.LockPath));
        Assert.Null(Auth.Identity.Peek(home.Config)?.Server); // not connected anywhere, and it still answers
    }

    [Fact]
    public async Task Without_a_database_file_nothing_is_printed_and_none_is_created()
    {
        Assert.False(File.Exists(home.Config.DatabasePath));

        var (code, output, error) = await Run();

        Assert.Equal((0, "", ""), (code, output, error));
        Assert.False(File.Exists(home.Config.DatabasePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task With_no_block_or_an_empty_one_nothing_is_printed(string? text)
    {
        Keep(text);
        if (text is not null)
        {
            using var store = new LocalStore(home.Config.DatabasePath);
            Assert.Equal("", store.Get(AgentConfig.MachinesContextKey));
        }

        Assert.Equal((0, "", ""), await Run());
    }

    [Fact]
    public async Task With_the_block_turned_off_on_this_machine_nothing_is_printed_even_if_one_is_stored()
    {
        Keep("block that must not be shown");

        Assert.Equal((0, "", ""), await Run(home.Config with { MachinesContextEnabled = false }));
    }

    [Fact]
    public async Task A_database_that_cannot_be_opened_prints_nothing_says_so_in_one_line_on_stderr_and_exits_zero()
    {
        await File.WriteAllTextAsync(home.Config.DatabasePath, "this is not a sqlite database, only text that must not be echoed");

        var (code, output, error) = await Run();

        Assert.Equal(0, code);
        Assert.Equal("", output);
        var line = Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("cm-agent hook SessionStart --context:", line, StringComparison.Ordinal);
        Assert.DoesNotContain("only text", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_is_one_json_object_whatever_the_text()
    {
        var parsed = JsonNode.Parse(Capture.SessionContext.Output("line1\nline2 \"q\" \\ \t"))!;

        Assert.Equal("line1\nline2 \"q\" \\ \t", (string?)parsed["hookSpecificOutput"]!["additionalContext"]);
    }
}
