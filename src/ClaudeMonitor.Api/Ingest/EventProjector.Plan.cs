using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Ingest;

/// <summary>
/// The Claude desktop app keeps a session's steps with its own tool instead of TodoWrite or TaskCreate: <c>update_plan</c> sends a whole list
/// (<c>items</c>) or a change to it (<c>advance</c>, <c>done</c> and <c>start</c> with 1-based step numbers), and <c>wrap_up</c> ends the turn and
/// checks off the step that was running. The steps are kept as <c>plan-0</c>, <c>plan-1</c> ... in step order. A shape that is not one of these changes nothing.
/// </summary>
public sealed partial class EventProjector
{
    public const string PlanTool = "mcp__ccd_turn__update_plan";
    public const string WrapUpTool = "mcp__ccd_turn__wrap_up";
    public const int PlanItemsMax = 12;
    private const string PlanPrefix = "plan-";

    private async Task PlanToolAsync(HarnessSession s, string tool, JsonElement input, DateTimeOffset at, CancellationToken ct)
    {
        if (tool == WrapUpTool)
        {
            foreach (var row in await PlanRowsAsync(s, ct).ConfigureAwait(false)) if (row.Status == "in_progress") Mark(row, "completed", at);
            return;
        }

        if (input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var steps = items.EnumerateArray().Take(PlanItemsMax).Where(i => i.ValueKind == JsonValueKind.Object && Str(i, "content") is { Length: > 0 }).ToList();
            if (steps.Count == 0) return; // a list with no step in it is not a plan: the steps stay as they were
            for (var count = 0; count < steps.Count; count++)
            {
                var id = PlanPrefix + count;
                var row = await FindTaskAsync(s, id, ct).ConfigureAwait(false);
                if (row is null)
                {
                    row = new SessionTask { SessionId = s.Id, ExternalId = id, CreatedAt = at.AddTicks(count * TimeSpan.TicksPerMicrosecond) }; // step order; the database keeps microseconds
                    db.SessionTasks.Add(row);
                }

                SetSubject(row, Str(steps[count], "content")!);
                row.Status = Str(steps[count], "status") is { } status && TaskStatus(status) && status != "deleted" ? status : "pending";
                row.UpdatedAt = at;
            }

            // The new list is the whole list: steps past its end are gone.
            foreach (var gone in (await PlanRowsAsync(s, ct).ConfigureAwait(false)).Where(r => PlanIndex(r) >= steps.Count)) Mark(gone, "deleted", at);
            return;
        }

        var rows = await PlanRowsAsync(s, ct).ConfigureAwait(false);
        SessionTask? At(int step) => rows.FirstOrDefault(r => PlanIndex(r) == step - 1);
        if (input.TryGetProperty("advance", out var advance) && advance.ValueKind == JsonValueKind.True)
        {
            var current = rows.FirstOrDefault(r => r.Status == "in_progress");
            if (current is not null) Mark(current, "completed", at);
            var after = current is null ? -1 : PlanIndex(current);
            if (rows.FirstOrDefault(r => r.Status == "pending" && PlanIndex(r) > after) is { } next) Mark(next, "in_progress", at);
        }

        if (input.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in done.EnumerateArray()) if (Step(n) is { } step && At(step) is { } row) Mark(row, "completed", at);
        }

        if (input.TryGetProperty("start", out var start) && Step(start) is { } from && At(from) is { } started) Mark(started, "in_progress", at);
    }

    /// <summary>A step number: only a JSON number is one (TryGetInt32 throws on anything else, which would drop the whole batch).</summary>
    private static int? Step(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;

    private static void Mark(SessionTask row, string status, DateTimeOffset at)
    {
        row.Status = status;
        row.UpdatedAt = at;
    }

    private static int PlanIndex(SessionTask row) => int.TryParse(row.ExternalId.AsSpan(PlanPrefix.Length), out var n) ? n : int.MaxValue;

    /// <summary>The session's steps still in the list (stored, and added by an earlier event of the same batch), in step order.</summary>
    private async Task<List<SessionTask>> PlanRowsAsync(HarnessSession s, CancellationToken ct)
    {
        var stored = await db.SessionTasks.Where(t => t.SessionId == s.Id && t.ExternalId.StartsWith(PlanPrefix)).ToListAsync(ct).ConfigureAwait(false);
        return [.. stored.Concat(db.SessionTasks.Local.Where(t => t.SessionId == s.Id && t.ExternalId.StartsWith(PlanPrefix, StringComparison.Ordinal)))
            .Distinct().Where(t => t.Status != "deleted").OrderBy(PlanIndex)];
    }
}
