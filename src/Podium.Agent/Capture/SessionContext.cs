using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Podium.Agent.Config;
using Podium.Agent.Push;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Capture;

/// <summary>
/// "aipod hook SessionStart --context" (ADR-0008 §3b): the synchronous SessionStart entry that hands the session the machines block
/// the daemon rendered, and the prompts sent to this session from the web that no hook has handed over yet (a session resumed after its
/// terminal closed, or one that was idle with no hook running). It reads the local database within a bounded time, prints the result as
/// additionalContext and always exits 0; it never talks to the network and never wakes the daemon (the recording hook does that). A prompt
/// handed over here is marked taken in the same step, so no other hook, nor the inbox push, hands it over again, and the daemon reports it
/// applied. When the print then fails, or the read runs out of its wait, the prompts taken go back to the queue (they are not lost, and the
/// web is not told "applied"). The text is wrapped and labelled as coming from the web, like every other way in. A missing, locked or broken database, or
/// nothing to hand over, prints nothing.
/// </summary>
public static class SessionContext
{
    public const string HookEvent = "SessionStart";
    public const string ContextOption = "--context";

    /// <summary>How long a hook whose read ran out of time waits for the read to notice it was stopped, before it hands back what was taken.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromMilliseconds(250);

    public static async Task<int> RunAsync(AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock, TextReader? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(clock);
#pragma warning disable CA1031 // a hook exits 0 whatever happens (project rule)
        var taken = new TakenPrompts();
        using var stop = new CancellationTokenSource();
        try
        {
            var read = Task.Run(() => Read(config, stdin, clock, taken, stop.Token));
            try
            {
                var text = await read.WaitAsync(config.ContextReadWait, clock);
                if (text is { Length: > 0 }) await stdout.WriteAsync(Output(text));
            }
            catch (Exception)
            {
                // the print failed or the read ran out of time: what it took is handed back, then the outer catch reports it
                await stop.CancelAsync();
                await GiveBack(config, read, taken, clock);
                throw;
            }
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

    /// <summary>Waits a moment for an abandoned read to notice it was stopped, then puts every prompt it took back to the queue (a take that lands later is put back by the read itself).</summary>
    private static async Task GiveBack(AgentConfig config, Task read, TakenPrompts taken, TimeProvider clock)
    {
        if (!read.IsCompleted)
        {
            try
            {
                await read.WaitAsync(StopGrace, clock);
            }
            catch (TimeoutException)
            {
                // the read is still busy (a stalled disk or pipe): the prompts it already took are still handed back below, the rest by the read itself
            }
        }

        if (taken.IsEmpty || !File.Exists(config.DatabasePath)) return;
        using var store = new LocalStore(config.DatabasePath);
        taken.GiveBackAll(store);
    }

    private static string? Read(AgentConfig config, TextReader? stdin, TimeProvider clock, TakenPrompts taken, CancellationToken stop)
    {
        if (!File.Exists(config.DatabasePath)) return null;
        var session = stdin is null ? null : SessionOf(stdin);
        using var store = new LocalStore(config.DatabasePath);
        var machines = config.MachinesContextEnabled && store.Get(AgentConfig.MachinesContextKey) is { } block ? Mcp.McpToolNames.Fill(block) : null; // the tool names of THIS session
        var pending = session is null ? null : Pending(config, store, session, clock.GetUtcNow(), taken, stop);
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
    private static string? Pending(AgentConfig config, LocalStore store, string session, DateTimeOffset now, TakenPrompts taken, CancellationToken stop)
    {
        var texts = new List<string>();
        while (!stop.IsCancellationRequested && store.TakeCommand(session, CommandKinds.Prompt, now) is { Body: { } } prompt)
        {
            taken.Add(prompt.Id);
            if (stop.IsCancellationRequested)
            {
                taken.GiveBack(store, prompt.Id); // the hook gave up while this was being taken: nothing will print it
                break;
            }

            texts.Add(ChannelEnvelopes.BuildInbox(prompt, config.PushContentMax, now));
        }

        return texts.Count == 0 ? null : "Messages sent to this session from Podium (the web) while it was not running:\n" + string.Join("\n", texts);
    }
}

/// <summary>
/// The prompts one hook run took. Whoever gives one back (the read that noticed the stop, or the hook handing back what was taken) removes it from
/// the set first and only then requeues it, so a prompt is requeued at most once per take: a second requeue would also undo a take of another hook.
/// </summary>
internal sealed class TakenPrompts
{
    private readonly ConcurrentDictionary<string, byte> ids = new();

    public bool IsEmpty => ids.IsEmpty;

    public void Add(string id) => ids[id] = 0;

    /// <summary>Puts one prompt back to the queue unless it was already given back.</summary>
    public void GiveBack(LocalStore store, string id)
    {
        if (ids.TryRemove(id, out _)) store.RequeueTakenCommand(id);
    }

    public void GiveBackAll(LocalStore store)
    {
        foreach (var id in ids.Keys) GiveBack(store, id);
    }
}
