using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>
/// "aipod hook SessionStart --context" (ADR-0008 §3b): the synchronous SessionStart entry that hands the session the machines block
/// the daemon rendered, and the prompts sent to this session from the web that no hook has handed over yet (a session resumed after its
/// terminal closed, or one that was idle with no hook running). It reads the local database within a bounded time, prints the result as
/// additionalContext and always exits 0; it never talks to the network and never wakes the daemon (the recording hook does that). A prompt
/// handed over here is marked taken in the same step, so no other hook, nor the inbox push, hands it over again, and the daemon reports it
/// applied. The text is wrapped and labelled as coming from the web, like every other way in. A missing, locked or broken database, or
/// nothing to hand over, prints nothing.
/// </summary>
public static class SessionContext
{
    public const string HookEvent = "SessionStart";
    public const string ContextOption = "--context";

    public static async Task<int> RunAsync(AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock, TextReader? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(clock);
#pragma warning disable CA1031 // a hook exits 0 whatever happens (project rule)
        try
        {
            var read = Task.Run(() => Read(config, stdin, clock));
            var text = await read.WaitAsync(config.ContextReadWait, clock);
            if (text is { Length: > 0 }) await stdout.WriteAsync(Output(text));
        }
        catch (Exception e)
        {
            // whatever happened (a timeout, a locked database, a native library that failed to load): the session is never blocked
            await stderr.WriteLineAsync($"aipod hook SessionStart --context: {e.GetType().Name}");
        }
#pragma warning restore CA1031

        return 0;
    }

    public static string Output(string text) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = HookEvent, ["additionalContext"] = text },
    }.ToJsonString();

    private static string? Read(AgentConfig config, TextReader? stdin, TimeProvider clock)
    {
        if (!File.Exists(config.DatabasePath)) return null;
        var session = stdin is null ? null : SessionOf(stdin);
        using var store = new LocalStore(config.DatabasePath);
        var machines = config.MachinesContextEnabled ? store.Get(AgentConfig.MachinesContextKey) : null;
        var pending = session is null ? null : Pending(config, store, session, clock.GetUtcNow());
        return machines is { Length: > 0 } && pending is not null ? machines + "\n\n" + pending : pending ?? machines;
    }

    private static string? SessionOf(TextReader stdin)
    {
        try
        {
            return JsonNode.Parse(stdin.ReadToEnd()) is JsonObject payload && payload["session_id"] is JsonValue id && id.TryGetValue<string>(out var session) && session.Length > 0
                ? session
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // not the hook's JSON: nothing to hand over
        }
    }

    /// <summary>The unexpired prompts queued for this session, taken once (oldest first) and wrapped as web input; null when there are none.</summary>
    private static string? Pending(AgentConfig config, LocalStore store, string session, DateTimeOffset now)
    {
        var texts = new List<string>();
        while (store.TakeCommand(session, CommandKinds.Prompt, now) is { Body: { } } prompt) texts.Add(ChannelEnvelopes.BuildInbox(prompt, config.PushContentMax, now));
        return texts.Count == 0 ? null : "Messages sent to this session from Claude Monitor (the web) while it was not running:\n" + string.Join("\n", texts);
    }
}
