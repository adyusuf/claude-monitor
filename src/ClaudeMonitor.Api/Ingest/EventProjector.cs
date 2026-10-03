using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Text;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Ingest;

/// <summary>
/// Folds one captured event into the session's projections (status, title, tasks, subagent runs, usage). The event
/// itself is stored as is; this only keeps the summaries current. Shapes follow Claude Code's hook payloads and
/// transcript entries; a payload that does not have the expected shape changes nothing (never an invented value).
/// </summary>
public sealed class EventProjector(MonitorDb db, ApiConfig config)
{
    public const int TitleMax = 80;
    public const int SubjectMax = 300;

    public async Task ApplyAsync(HarnessSession s, CapturedEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(e);
        if (e.OccurredAt > s.LastEventAt) s.LastEventAt = e.OccurredAt;
        var p = e.Payload;
        switch (e.Kind)
        {
            case "hook:SessionStart":
                s.Status = SessionStatuses.Active; // a resumed session reopens, even after it ended
                s.EndedAt = null;
                s.Model = Str(p, "model") ?? s.Model;
                SetTitle(s, Str(p, "session_title"), overwrite: true);
                break;
            case "hook:UserPromptSubmit":
                SetStatus(s, SessionStatuses.Active);
                SetTitle(s, Str(p, "prompt"), overwrite: false);
                break;
            case "hook:PreToolUse":
                SetStatus(s, SessionStatuses.Active);
                break;
            case "hook:PostToolUse":
                SetStatus(s, SessionStatuses.Active);
                await TaskToolAsync(s, p, e.OccurredAt, ct);
                break;
            case "hook:PermissionRequest":
            case "hook:Notification":
                SetStatus(s, SessionStatuses.Waiting);
                break;
            case "hook:Stop":
                SetStatus(s, SessionStatuses.Idle);
                break;
            case "hook:SubagentStart":
                await SubagentAsync(s, p, e.OccurredAt, finished: false, ct);
                break;
            case "hook:SubagentStop":
                await SubagentAsync(s, p, e.OccurredAt, finished: true, ct);
                break;
            case "hook:SessionEnd":
                s.Status = SessionStatuses.Ended;
                s.EndedAt = e.OccurredAt;
                break;
            case EventKinds.Transcript:
                SetTitle(s, Str(p, "customTitle") ?? Str(p, "agentName"), overwrite: true);
                break;
            case EventKinds.Usage:
                await UsageAsync(s, p, e.OccurredAt, ct);
                break;
            default:
                break;
        }
    }

    private static void SetStatus(HarnessSession s, string status)
    {
        if (s.Status != SessionStatuses.Ended) s.Status = status;
    }

    private static void SetTitle(HarnessSession s, string? value, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(value) || (!overwrite && s.Title is not null)) return;
        var line = value.Trim().Split('\n')[0].Trim();
        s.Title = line.Length > TitleMax ? line[..TitleMax] : line;
        s.TitleSearch = SearchText.Normalize(s.Title);
    }

    private async Task TaskToolAsync(HarnessSession s, JsonElement p, DateTimeOffset at, CancellationToken ct)
    {
        if (Str(p, "agent_id") is not null) return; // a subagent's own list is not the session's
        var input = p.TryGetProperty("tool_input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        if (input.ValueKind != JsonValueKind.Object) return;
        switch (Str(p, "tool_name"))
        {
            case "TodoWrite" when input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array:
                // TodoWrite sends the whole list: what is no longer in it is gone.
                var keep = new HashSet<string>(StringComparer.Ordinal);
                foreach (var todo in todos.EnumerateArray())
                {
                    if (Str(todo, "content") is { } content && Str(todo, "status") is { } status)
                    {
                        var id = $"todo-{keep.Count}";
                        keep.Add(id);
                        await UpsertTaskAsync(s, id, content, status, at, ct);
                    }
                }

                var stored = await db.SessionTasks.Where(t => t.SessionId == s.Id && t.ExternalId.StartsWith("todo-")).ToListAsync(ct);
                foreach (var gone in stored.Concat(db.SessionTasks.Local).Where(t => t.SessionId == s.Id
                             && t.ExternalId.StartsWith("todo-", StringComparison.Ordinal) && !keep.Contains(t.ExternalId)))
                {
                    gone.Status = "deleted";
                    gone.UpdatedAt = at;
                }

                break;
            case "TaskCreate" when CreatedId(p) is { } id && Str(input, "subject") is { } subject:
                await UpsertTaskAsync(s, id, subject, "pending", at, ct);
                break;
            case "TaskUpdate" when (Str(input, "taskId") ?? Num(input, "taskId")) is { } id:
                var row = await FindTaskAsync(s, id, ct);
                if (row is null) return;
                if (Str(input, "status") is { } st && TaskStatus(st)) row.Status = st;
                if (Str(input, "subject") is { } subj) SetSubject(row, subj);
                row.UpdatedAt = at;
                break;
            default:
                break;
        }
    }

    private static bool TaskStatus(string s) => s is "pending" or "in_progress" or "completed" or "deleted";

    private static string? CreatedId(JsonElement p)
    {
        if (!p.TryGetProperty("tool_response", out var r)) return null;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
        {
            return Str(task, "id") ?? Num(task, "id");
        }

        var text = r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var m = text is null ? null : System.Text.RegularExpressions.Regex.Match(text, @"^Task #(\S+) created successfully");
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    private async Task<SessionTask?> FindTaskAsync(HarnessSession s, string externalId, CancellationToken ct) =>
        db.SessionTasks.Local.FirstOrDefault(t => t.SessionId == s.Id && t.ExternalId == externalId)
        ?? await db.SessionTasks.FirstOrDefaultAsync(t => t.SessionId == s.Id && t.ExternalId == externalId, ct);

    private async Task UpsertTaskAsync(HarnessSession s, string externalId, string subject, string status, DateTimeOffset at,
        CancellationToken ct)
    {
        var row = await FindTaskAsync(s, externalId, ct);
        if (row is null)
        {
            row = new SessionTask { SessionId = s.Id, ExternalId = externalId, CreatedAt = at };
            db.SessionTasks.Add(row);
        }

        SetSubject(row, subject);
        if (TaskStatus(status)) row.Status = status;
        row.UpdatedAt = at;
    }

    private static void SetSubject(SessionTask row, string subject)
    {
        row.Subject = subject.Length > SubjectMax ? subject[..SubjectMax] : subject;
        row.SubjectSearch = SearchText.Normalize(row.Subject);
    }

    private async Task SubagentAsync(HarnessSession s, JsonElement p, DateTimeOffset at, bool finished, CancellationToken ct)
    {
        if (Str(p, "agent_id") is not { } id) return;
        var run = db.SubagentRuns.Local.FirstOrDefault(r => r.SessionId == s.Id && r.ExternalId == id)
                  ?? await db.SubagentRuns.FirstOrDefaultAsync(r => r.SessionId == s.Id && r.ExternalId == id, ct);
        if (run is null)
        {
            run = new SubagentRun { SessionId = s.Id, ExternalId = id, StartedAt = at };
            db.SubagentRuns.Add(run);
        }

        run.AgentType = Str(p, "agent_type") ?? (run.AgentType.Length > 0 ? run.AgentType : "general-purpose");
        run.Description ??= Str(p, "description");
        if (finished)
        {
            run.Status = "finished";
            run.EndedAt = at;
        }
    }

    private async Task UsageAsync(HarnessSession s, JsonElement p, DateTimeOffset at, CancellationToken ct)
    {
        if (Str(p, "model") is not { } model) return;
        var row = db.SessionUsage.Local.FirstOrDefault(u => u.SessionId == s.Id && u.Model == model)
                  ?? await db.SessionUsage.FirstOrDefaultAsync(u => u.SessionId == s.Id && u.Model == model, ct);
        if (row is null)
        {
            row = new SessionUsage { SessionId = s.Id, Model = model };
            db.SessionUsage.Add(row);
        }

        long Tokens(string name) => p.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) && n > 0 ? n : 0;
        var w5 = Tokens("cacheWrite5mTokens");
        var w1 = Tokens("cacheWrite1hTokens");
        row.InputTokens += Tokens("inputTokens");
        row.OutputTokens += Tokens("outputTokens");
        row.CacheReadTokens += Tokens("cacheReadTokens");
        row.CacheWriteTokens += w5 + w1;
        row.UpdatedAt = at;
        s.Model ??= model;
        row.CostUsd = config.PriceFor(model) is { } price
            ? (row.CostUsd ?? 0m) + ((Tokens("inputTokens") * price.Input) + (Tokens("outputTokens") * price.Output)
                + (Tokens("cacheReadTokens") * price.CacheRead) + (w5 * price.CacheWrite) + (w1 * price.Input * 2m)) / 1_000_000m
            : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetRawText()
            : null;
}
