using System.Text.Json;
using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Ingest;

/// <summary>
/// A subagent is normally reported by the SubagentStart and SubagentStop hooks. When those do not arrive (a harness that does not send them for
/// every kind of agent), the call that started it still tells: the <c>Agent</c> tool (<c>Task</c> in older versions) with its <c>subagent_type</c>
/// and <c>description</c>. The run is keyed by the agent id the tool's response names, so a run the hooks reported too stays one row; with no
/// agent id the call's own id stands in. A call that does not name an id at all, or a nested agent's call, is not guessed at.
/// </summary>
public sealed partial class EventProjector
{
    private async Task SubagentToolAsync(HarnessSession s, JsonElement p, JsonElement input, DateTimeOffset at, CancellationToken ct)
    {
        var response = p.TryGetProperty("tool_response", out var r) ? r : default;
        var id = Str(response, "agentId") ?? Str(response, "agent_id") ?? (Str(p, "tool_use_id") is { } call ? $"tool-{call}" : null);
        if (id is null || id.Length > 128) return;

        var run = db.SubagentRuns.Local.FirstOrDefault(x => x.SessionId == s.Id && x.ExternalId == id)
                  ?? await db.SubagentRuns.FirstOrDefaultAsync(x => x.SessionId == s.Id && x.ExternalId == id, ct).ConfigureAwait(false);
        if (run is null)
        {
            run = new SubagentRun { SessionId = s.Id, ExternalId = id, StartedAt = at };
            db.SubagentRuns.Add(run);
        }

        // The hooks name the kind of agent when they arrive; the call's own words only fill what is still missing.
        if (run.AgentType.Length == 0 || run.AgentType == "general-purpose") run.AgentType = Str(input, "subagent_type") is { Length: > 0 } kind ? kind : "general-purpose";
        run.Description ??= Str(input, "description") is { Length: > 0 } text ? text[..Math.Min(text.Length, SubjectMax)] : null;
        // The call answers when a foreground agent is done; one run in the background answers at once and its own stop says when it ended.
        var background = input.TryGetProperty("run_in_background", out var b) && b.ValueKind == JsonValueKind.True;
        if (!background && run.Status != "finished")
        {
            run.Status = "finished";
            run.EndedAt = at;
        }
    }
}
