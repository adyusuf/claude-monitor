using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>
/// "cm-agent hook SessionStart --context" (ADR-0008 §3b): the synchronous SessionStart entry that hands the session the machines block
/// the daemon rendered. It reads one value from the local database, within a bounded time, prints it as additionalContext and
/// always exits 0; it never talks to the network and never wakes the daemon (the recording hook does that). A missing, locked or
/// broken database, or an empty block, prints nothing.
/// </summary>
public static class SessionContext
{
    public const string HookEvent = "SessionStart";
    public const string ContextOption = "--context";

    public static async Task<int> RunAsync(AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(clock);
        if (!config.MachinesContextEnabled) return 0;
        try
        {
            var read = Task.Run(() => Read(config));
            var text = await read.WaitAsync(config.ContextReadWait, clock);
            if (text is { Length: > 0 }) await stdout.WriteAsync(Output(text));
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Microsoft.Data.Sqlite.SqliteException)
        {
            await stderr.WriteLineAsync($"cm-agent hook SessionStart --context: {e.GetType().Name}");
        }

        return 0;
    }

    public static string Output(string text) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = HookEvent, ["additionalContext"] = text },
    }.ToJsonString();

    private static string? Read(AgentConfig config)
    {
        if (!File.Exists(config.DatabasePath)) return null;
        using var store = new LocalStore(config.DatabasePath);
        return store.Get(AgentConfig.MachinesContextKey);
    }
}
