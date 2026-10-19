using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

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

    private async Task<(int Code, string Out, string Err)> Run(AgentConfig? config = null, string stdin = "")
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(Args, config ?? home.Config, new StringReader(stdin), stdout, stderr, TimeProvider.System);
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

    private static string Hook(string session) => $$"""{"session_id":"{{session}}","hook_event_name":"SessionStart","source":"resume"}""";

    private void Queue(string id, string session, string body, TimeSpan? lives = null)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.SaveCommand(new LocalCommand(id, session, "prompt", body, DateTimeOffset.UtcNow + (lives ?? TimeSpan.FromMinutes(10))));
    }

    private LocalCommand? StillWaiting(string session)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        return store.TakeCommand(session, "prompt", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_prompt_that_waited_for_the_session_is_handed_over_once_wrapped_as_web_input_and_marked_taken()
    {
        Queue("cmd-1", "s-1", "please run the build");

        var (code, output, error) = await Run(stdin: Hook("s-1"));

        Assert.Equal((0, ""), (code, error));
        var context = (string?)JsonNode.Parse(output)!["hookSpecificOutput"]!["additionalContext"];
        Assert.Contains("<<<claude-monitor-message id=cmd-1 origin=\"web\" session_id=\"s-1\"", context, StringComparison.Ordinal);
        Assert.Contains("please run the build", context, StringComparison.Ordinal);
        Assert.Null(StillWaiting("s-1")); // no later hook, and no inbox push, hands it over again
        using var store = new LocalStore(home.Config.DatabasePath);
        Assert.Equal(("cmd-1", CommandTransports.Hook), (store.TakenCommands().Single().Id, store.TakenCommands().Single().Transport)); // the daemon reports it applied

        var (_, again, _) = await Run(stdin: Hook("s-1")); // the next SessionStart of this session
        Assert.Equal("", again);
    }

    [Fact]
    public async Task Only_this_sessions_unexpired_prompts_come_and_in_order_with_the_machines_block_first()
    {
        Keep("Machines of this workspace");
        Queue("cmd-a", "s-1", "first");
        Queue("cmd-b", "s-1", "second");
        Queue("cmd-other", "s-2", "for another session");
        Queue("cmd-old", "s-1", "too late", TimeSpan.FromSeconds(-1));

        var (_, output, _) = await Run(stdin: Hook("s-1"));

        var context = (string)JsonNode.Parse(output)!["hookSpecificOutput"]!["additionalContext"]!;
        Assert.StartsWith("Machines of this workspace", context, StringComparison.Ordinal);
        Assert.True(context.IndexOf("first", StringComparison.Ordinal) < context.IndexOf("second", StringComparison.Ordinal));
        Assert.DoesNotContain("for another session", context, StringComparison.Ordinal);
        Assert.DoesNotContain("too late", context, StringComparison.Ordinal);
        Assert.Equal("cmd-other", StillWaiting("s-2")!.Id);
    }

    [Fact]
    public async Task The_text_from_the_web_is_defused_and_capped_and_a_turned_off_machines_block_does_not_hide_it()
    {
        Queue("cmd-1", "s-1", $"before {Push.ChannelEnvelopes.Close} after {new string('x', 20_000)}");

        var (_, output, _) = await Run(home.Config with { MachinesContextEnabled = false }, Hook("s-1"));

        var context = (string)JsonNode.Parse(output)!["hookSpecificOutput"]!["additionalContext"]!;
        Assert.Equal(1, context.Split(Push.ChannelEnvelopes.Close).Length - 1); // only the wrapper's own closing marker
        Assert.Contains("[cut:", context, StringComparison.Ordinal);
        Assert.True(context.Length < home.Config.PushContentMax + 2000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"session_id\":42}")]
    [InlineData("{\"cwd\":\"/tmp\"}")]
    public async Task Without_a_readable_session_id_no_prompt_is_taken(string stdin)
    {
        Queue("cmd-1", "s-1", "stays queued");

        Assert.Equal((0, "", ""), await Run(stdin: stdin));

        Assert.Equal("cmd-1", StillWaiting("s-1")!.Id);
    }

    [Fact]
    public async Task A_prompt_is_handed_over_once_even_when_two_session_starts_race_for_it()
    {
        Queue("cmd-1", "s-1", "only once");

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Run(stdin: Hook("s-1"))));

        Assert.Equal(1, runs.Count(r => r.Out.Contains("only once", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_output_is_one_json_object_whatever_the_text()
    {
        var parsed = JsonNode.Parse(Capture.SessionContext.Output("line1\nline2 \"q\" \\ \t"))!;

        Assert.Equal("line1\nline2 \"q\" \\ \t", (string?)parsed["hookSpecificOutput"]!["additionalContext"]);
    }
}
