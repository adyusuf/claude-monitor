using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
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

    /// <summary>
    /// At the end of a turn: a stop ends it; a queued prompt continues it (waiting up to CM_STOP_WAIT, but only while
    /// the web can be heard; otherwise one look at what already arrived).
    /// </summary>
    private async Task<string?> StopAsync(string session, CancellationToken ct)
    {
        var wait = config.StopWait > TimeSpan.Zero && !CanHearTheWeb() ? TimeSpan.Zero : config.StopWait;
        var deadline = clock.GetUtcNow() + wait;
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

    /// <summary>
    /// Asks the web and waits for the owner's answer; no answer in time means no decision (Claude asks here). When the
    /// web cannot be heard, nothing is asked: a request sent later would be stale.
    /// </summary>
    private async Task<string?> PermissionAsync(string session, JsonObject payload, CancellationToken ct)
    {
        if (!CanHearTheWeb()) return null;
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

    /// <summary>
    /// Whether an answer from the web can arrive: the agent is logged in (identity and a token) and the daemon's last
    /// answered heartbeat is recent. Read only by the hooks that would wait: the credential store may start a process.
    /// </summary>
    private bool CanHearTheWeb()
    {
        if (!Identity.Load(config).Connected) return false;
        var credentials = Credentials.For(config);
        if (credentials.Read(Credentials.Refresh) is null && credentials.Read(Credentials.Access) is null) return false;
        if (!DateTimeOffset.TryParse(store.Get(Relay.LastContactKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var contact))
        {
            return false;
        }

        return clock.GetUtcNow() - contact < config.HeartbeatEvery * 3; // a future contact (clock skew) counts as fresh
    }
}
