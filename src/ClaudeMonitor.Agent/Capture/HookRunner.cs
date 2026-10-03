using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>
/// "cm-agent hook &lt;Event&gt;": one hook call from Claude Code. It records the payload in the local database and,
/// for the hooks that can carry an answer, hands the session what the web sent: a stop (PreToolUse, Stop), a prompt
/// (Stop, UserPromptSubmit) or a permission decision (PermissionRequest). It never blocks a session: any failure is
/// written to stderr and the hook exits 0 with no decision (project rule).
/// </summary>
public sealed class HookRunner(AgentConfig config, LocalStore store, TimeProvider clock)
{
    public const string StopReason = "Stopped from Claude Monitor (sent from the web).";

    /// <summary>Returns what the hook prints on stdout (null: nothing).</summary>
    public async Task<string?> RunAsync(string hookEvent, string input, CancellationToken ct)
    {
        if (JsonNode.Parse(input) is not JsonObject payload || payload["session_id"]?.GetValue<string>() is not { Length: > 0 } session)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var project = ProjectInfo.Resolve(payload["cwd"]?.GetValue<string>());
        Record(hookEvent, session, payload, project, now);
        if (payload["transcript_path"]?.GetValue<string>() is { Length: > 0 } transcript)
        {
            store.TrackTranscript(new TranscriptCursor(session, HarnessKinds.ClaudeCode, transcript, 0, project?.Key, project?.Name,
                project?.Branch), now);
        }

        return hookEvent switch
        {
            "PreToolUse" => store.TakeCommand(session, CommandKinds.Stop, now) is null ? null : StopOutput(),
            "Stop" => await StopAsync(session, ct),
            "UserPromptSubmit" => PromptContext(session, now),
            "PermissionRequest" => await PermissionAsync(session, payload, ct),
            _ => null,
        };
    }

    private void Record(string hookEvent, string session, JsonObject payload, ProjectInfo? project, DateTimeOffset now) =>
        new Recorder(config, store).Record(EventKinds.Hook(hookEvent), session, payload, project, now);

    private static string StopOutput() => new JsonObject { ["continue"] = false, ["stopReason"] = StopReason }.ToJsonString();

    /// <summary>At the end of a turn: a stop ends it; a queued prompt continues it (waiting up to CM_STOP_WAIT).</summary>
    private async Task<string?> StopAsync(string session, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + config.StopWait;
        while (true)
        {
            var now = clock.GetUtcNow();
            if (store.TakeCommand(session, CommandKinds.Stop, now) is not null) return null; // it is stopping anyway
            if (store.TakeCommand(session, CommandKinds.Prompt, now) is { } prompt)
            {
                return new JsonObject { ["decision"] = "block", ["reason"] = prompt.Body }.ToJsonString();
            }

            if (now >= deadline) return null;
            await Task.Delay(config.PollEvery, clock, ct);
        }
    }

    /// <summary>Prompts sent from the web while the user typed one: they ride along as context.</summary>
    private string? PromptContext(string session, DateTimeOffset now)
    {
        var texts = new List<string>();
        while (store.TakeCommand(session, CommandKinds.Prompt, now) is { Body: { } body }) texts.Add(body);
        if (texts.Count == 0) return null;
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "UserPromptSubmit",
                ["additionalContext"] = "Messages sent to this session from Claude Monitor (the web):\n- " + string.Join("\n- ", texts),
            },
        }.ToJsonString();
    }

    /// <summary>Asks the web and waits for the owner's answer; no answer in time means no decision (Claude asks here).</summary>
    private async Task<string?> PermissionAsync(string session, JsonObject payload, CancellationToken ct)
    {
        var localId = Guid.NewGuid().ToString("N");
        var input = Masker.Mask(payload["tool_input"]?.DeepClone() ?? new JsonObject())!.ToJsonString();
        store.AddPermission(new PermissionAsk(localId, HarnessKinds.ClaudeCode, session, payload["tool_name"]?.GetValue<string>() ?? "unknown",
            input, (int)config.PermissionWait.TotalSeconds, clock.GetUtcNow(), null, null, null, PermissionStates.New));
        var deadline = clock.GetUtcNow() + config.PermissionWait;
        while (clock.GetUtcNow() < deadline)
        {
            if (store.Permission(localId) is { State: PermissionStates.Answered, Decision: { } decision } answered)
            {
                var result = new JsonObject { ["behavior"] = decision == PermissionDecisions.Allow ? "allow" : "deny" };
                if (decision != PermissionDecisions.Allow && answered.Reason is { } why) result["message"] = why;
                return new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = result },
                }.ToJsonString();
            }

            await Task.Delay(config.PollEvery, clock, ct);
        }

        store.PermissionExpired(localId);
        return null;
    }
}
