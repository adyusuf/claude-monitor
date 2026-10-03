using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class HookRunnerTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalStore store;

    public HookRunnerTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    private Task<string?> Run(string hook, object payload) =>
        new HookRunner(home.Config, store, clock).RunAsync(hook, JsonSerializer.Serialize(payload), CancellationToken.None);

    private List<CapturedEvent> Queued() => store.NextBatch(1000, int.MaxValue)?.Rows.Select(r => r.Event).ToList() ?? [];

    [Fact]
    public async Task A_hook_payload_is_queued_with_its_project_and_secrets_masked()
    {
        var output = await Run("PostToolUse", new
        {
            session_id = "s1",
            cwd = home.Dir,
            tool_name = "Bash",
            tool_input = new { command = "export GITHUB_TOKEN=ghp_" + new string('a', 36) },
        });
        Assert.Null(output);
        var e = Assert.Single(Queued());
        Assert.Equal("hook:PostToolUse", e.Kind);
        Assert.Equal("s1", e.SessionExternalId);
        Assert.StartsWith("dir:", e.ProjectKey, StringComparison.Ordinal);
        var command = e.Payload.GetProperty("tool_input").GetProperty("command").GetString();
        Assert.Contains("[masked:", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_aaaa", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masking_can_be_off_and_an_oversized_payload_becomes_a_marker()
    {
        store.Set("settings.mask_secrets", "false");
        store.Set("settings.event_max_bytes", "200");
        await Run("PostToolUse", new { session_id = "s1", tool_name = "Read", tool_response = new string('x', 500) + " password=hunter2hunter2" });
        var e = Assert.Single(Queued());
        Assert.True(e.Truncated);
        Assert.True(e.Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal("Read", e.Payload.GetProperty("tool_name").GetString());
        await Run("PostToolUse", new { session_id = "s1", note = "password=hunter2hunter2" });
        store.Acknowledge(1);
        Assert.Contains("hunter2hunter2", Queued()[0].Payload.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_or_a_payload_without_a_session_is_ignored()
    {
        Assert.Null(await Run("Stop", new { cwd = "/" }));
        Assert.Null(await new HookRunner(home.Config, store, clock).RunAsync("Stop", "[1,2]", CancellationToken.None));
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task A_stop_from_the_web_ends_the_turn_at_the_next_tool_call_once()
    {
        store.SaveCommand(new LocalCommand("c1", "s1", CommandKinds.Stop, null, clock.GetUtcNow().AddMinutes(5)));
        var output = JsonNode.Parse((await Run("PreToolUse", new { session_id = "s1", tool_name = "Bash" }))!)!;
        Assert.False(output["continue"]!.GetValue<bool>());
        Assert.Null(await Run("PreToolUse", new { session_id = "s1", tool_name = "Bash" }));
        Assert.Equal(["c1"], store.TakenCommands());
    }

    [Fact]
    public async Task A_prompt_from_the_web_continues_the_session_at_stop()
    {
        store.SaveCommand(new LocalCommand("c1", "s1", CommandKinds.Prompt, "Now run the tests", clock.GetUtcNow().AddMinutes(5)));
        store.SaveCommand(new LocalCommand("c2", "other", CommandKinds.Prompt, "not mine", clock.GetUtcNow().AddMinutes(5)));
        var output = JsonNode.Parse((await Run("Stop", new { session_id = "s1" }))!)!;
        Assert.Equal("block", output["decision"]!.GetValue<string>());
        Assert.Equal("Now run the tests", output["reason"]!.GetValue<string>());
        Assert.Null(await Run("Stop", new { session_id = "s1" }));
    }

    [Fact]
    public async Task An_expired_command_is_not_delivered_and_a_stop_wins_over_a_prompt_at_stop()
    {
        store.SaveCommand(new LocalCommand("old", "s1", CommandKinds.Prompt, "too late", clock.GetUtcNow().AddMinutes(-1)));
        Assert.Null(await Run("Stop", new { session_id = "s1" }));
        store.SaveCommand(new LocalCommand("p", "s1", CommandKinds.Prompt, "x", clock.GetUtcNow().AddMinutes(5)));
        store.SaveCommand(new LocalCommand("s", "s1", CommandKinds.Stop, null, clock.GetUtcNow().AddMinutes(5)));
        Assert.Null(await Run("Stop", new { session_id = "s1" }));
    }

    [Fact]
    public async Task Prompts_sent_while_the_user_types_ride_along_as_context()
    {
        store.SaveCommand(new LocalCommand("c1", "s1", CommandKinds.Prompt, "first", clock.GetUtcNow().AddMinutes(5)));
        store.SaveCommand(new LocalCommand("c2", "s1", CommandKinds.Prompt, "second", clock.GetUtcNow().AddMinutes(5)));
        var output = JsonNode.Parse((await Run("UserPromptSubmit", new { session_id = "s1", prompt = "hi" }))!)!;
        var context = output["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        Assert.Contains("- first\n- second", context, StringComparison.Ordinal);
        Assert.Null(await Run("UserPromptSubmit", new { session_id = "s1", prompt = "again" }));
    }

    [Fact]
    public async Task A_permission_answer_from_the_web_becomes_the_hooks_decision()
    {
        // The hook and the daemon are two processes with two connections: so are the runner and this test.
        using var daemon = new LocalStore(home.Config.DatabasePath);
        var running = Run("PermissionRequest", new { session_id = "s1", tool_name = "Bash", tool_input = new { command = "git push" } });
        var asked = await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        Assert.Equal("Bash", asked.ToolName);
        daemon.PermissionSent(asked.LocalId, "remote-1");
        daemon.PermissionAnswered("remote-1", PermissionDecisions.Deny, "not on Friday");
        var output = JsonNode.Parse((await running)!)!["hookSpecificOutput"]!;
        Assert.Equal("PermissionRequest", output["hookEventName"]!.GetValue<string>());
        Assert.Equal("deny", output["decision"]!["behavior"]!.GetValue<string>());
        Assert.Equal("not on Friday", output["decision"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_allow_has_no_message_and_no_answer_in_time_gives_no_decision()
    {
        using var daemon = new LocalStore(home.Config.DatabasePath);
        var running = Run("PermissionRequest", new { session_id = "s1", tool_name = "Edit" });
        var asked = await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        daemon.PermissionSent(asked.LocalId, "r2");
        daemon.PermissionAnswered("r2", PermissionDecisions.Allow, "fine");
        var decision = JsonNode.Parse((await running)!)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", decision["behavior"]!.GetValue<string>());
        Assert.Null(decision["message"]);

        var unanswered = Run("PermissionRequest", new { session_id = "s1", tool_name = "Edit" });
        await WaitFor(() => daemon.PermissionsIn(PermissionStates.New).SingleOrDefault());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(await unanswered);
        Assert.Single(daemon.PermissionsIn(PermissionStates.Expired));
    }

    [Fact]
    public async Task A_transcript_path_is_remembered_for_the_tailer()
    {
        await Run("SessionStart", new { session_id = "s1", transcript_path = "/tmp/x.jsonl", cwd = home.Dir });
        var t = Assert.Single(store.Transcripts(clock.GetUtcNow().AddMinutes(-1)));
        Assert.Equal("/tmp/x.jsonl", t.Path);
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe)
        where T : class
    {
        for (var i = 0; i < 500; i++)
        {
            if (probe() is { } found) return found;
            await Task.Delay(10);
        }

        throw new TimeoutException();
    }
}
