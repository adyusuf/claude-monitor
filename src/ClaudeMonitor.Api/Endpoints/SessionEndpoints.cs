using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record SessionRow(Guid Id, string HarnessKind, string? Title, string? Model, string? GitBranch, string Status,
    DateTimeOffset StartedAt, DateTimeOffset LastEventAt, DateTimeOffset? EndedAt, Guid? ProjectId, string? ProjectName,
    Guid AgentId, string Hostname, Guid OwnerId, string OwnerName, decimal? CostUsd, int OpenPermissions);
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);
public sealed record TaskRow(Guid Id, string ExternalId, string Subject, string Status, DateTimeOffset UpdatedAt);
public sealed record SubagentRow(Guid Id, string AgentType, string? Description, string Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
public sealed record UsageRow(string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal? CostUsd);
public sealed record EventRow(long Id, string Kind, DateTimeOffset OccurredAt, bool Truncated, System.Text.Json.JsonDocument Payload);
public sealed record SessionDetail(SessionRow Session, bool CanCommand, IReadOnlyList<TaskRow> Tasks, IReadOnlyList<SubagentRow> Subagents,
    IReadOnlyList<UsageRow> Usage);

/// <summary>Reading sessions: paged by key, newest first; any member of the workspace may read.</summary>
public static class SessionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{id:guid}/sessions", List);
        g.MapGet("/workspaces/{id:guid}/stream", Stream);
        g.MapGet("/sessions/{id:guid}", Detail);
        g.MapGet("/sessions/{id:guid}/events", Events);
    }

    private static async Task<IResult> List(Guid id, string? q, string? status, string? cursor, int? limit, HttpContext http,
        MonitorDb db, ApiConfig config)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.HarnessSessions.AsNoTracking().Where(s => s.WorkspaceId == id);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = SearchText.ContainsPattern(q);
            query = query.Where(s => EF.Functions.Like(s.TitleSearch, pattern, "\\")
                || db.Projects.Any(p => p.Id == s.ProjectId && EF.Functions.Like(p.DisplayNameSearch, pattern, "\\")));
        }

        if (status is { Length: > 0 }) query = query.Where(s => s.Status == status);
        if (Cursor.Parse(cursor) is { } after)
        {
            query = query.Where(s => s.LastEventAt < after.At || (s.LastEventAt == after.At && s.Id.CompareTo(after.Id) < 0));
        }

        var page = await query.OrderByDescending(s => s.LastEventAt).ThenByDescending(s => s.Id).Take(take + 1)
            .ToListAsync(http.RequestAborted);
        var rows = await RowsAsync(db, page.Take(take).ToList(), http.RequestAborted);
        var next = page.Count > take ? Cursor.Of(page[take - 1].LastEventAt, page[take - 1].Id) : null;
        return Results.Ok(new Page<SessionRow>(rows, next));
    }

    internal static async Task<List<SessionRow>> RowsAsync(MonitorDb db, List<HarnessSession> sessions, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var agentIds = sessions.Select(s => s.AgentId).Distinct().ToList();
        var projectIds = sessions.Where(s => s.ProjectId != null).Select(s => s.ProjectId!.Value).Distinct().ToList();
        var agents = await (from a in db.Agents.AsNoTracking()
                            join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                            join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                            where agentIds.Contains(a.Id)
                            select new { a.Id, m.Hostname, a.UserId, u.DisplayName }).ToDictionaryAsync(x => x.Id, ct);
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var usage = await db.SessionUsage.AsNoTracking().Where(u => ids.Contains(u.SessionId)).ToListAsync(ct);
        var open = await db.PermissionRequests.AsNoTracking()
            .Where(p => ids.Contains(p.SessionId) && p.Status == PermissionStatuses.Open)
            .GroupBy(p => p.SessionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return sessions.Select(s =>
        {
            var a = agents[s.AgentId];
            var mine = usage.Where(u => u.SessionId == s.Id).ToList();
            decimal? cost = mine.Count == 0 ? 0m : mine.Any(u => u.CostUsd is null) ? null : mine.Sum(u => u.CostUsd!.Value);
            return new SessionRow(s.Id, s.HarnessKind, s.Title, s.Model, s.GitBranch, s.Status, s.StartedAt, s.LastEventAt,
                s.EndedAt, s.ProjectId, s.ProjectId is { } pid ? projects.GetValueOrDefault(pid) : null, s.AgentId, a.Hostname,
                a.UserId, a.DisplayName, cost, open.GetValueOrDefault(s.Id));
        }).ToList();
    }

    private static async Task<IResult> Detail(Guid id, HttpContext http, MonitorDb db)
    {
        var userId = http.User.UserId();
        if (await Access.SessionAsync(db, userId, id, http.RequestAborted) is not { } found) return Http.NotFound();
        var row = (await RowsAsync(db, [found.Session], http.RequestAborted))[0];
        var tasks = await db.SessionTasks.AsNoTracking().Where(t => t.SessionId == id && t.Status != "deleted")
            .OrderBy(t => t.CreatedAt).Select(t => new TaskRow(t.Id, t.ExternalId, t.Subject, t.Status, t.UpdatedAt))
            .ToListAsync(http.RequestAborted);
        var subagents = await db.SubagentRuns.AsNoTracking().Where(r => r.SessionId == id).OrderByDescending(r => r.StartedAt)
            .Select(r => new SubagentRow(r.Id, r.AgentType, r.Description, r.Status, r.StartedAt, r.EndedAt)).ToListAsync(http.RequestAborted);
        var usage = await db.SessionUsage.AsNoTracking().Where(u => u.SessionId == id).OrderBy(u => u.Model)
            .Select(u => new UsageRow(u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CostUsd))
            .ToListAsync(http.RequestAborted);
        var canCommand = await Access.OwnsSessionAsync(db, userId, found.Session, http.RequestAborted);
        return Results.Ok(new SessionDetail(row, canCommand, tasks, subagents, usage));
    }

    private const int MaxKinds = 8;

    private static async Task<IResult> Events(Guid id, long? before, int? limit, string? kind, HttpContext http, MonitorDb db, ApiConfig config)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.SessionEvents.AsNoTracking().Where(e => e.SessionId == id);
        if (before is { } b) query = query.Where(e => e.Id < b);
        if (kind is { Length: > 0 })
        {
            // One kind, or several separated by commas (the conversation view asks for the transcript and a few hooks at once).
            var kinds = kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(MaxKinds).ToArray();
            if (kinds.Length == 1) query = query.Where(e => e.Kind == kinds[0]);
            else if (kinds.Length > 1) query = query.Where(e => kinds.Contains(e.Kind));
        }

        var rows = await query.OrderByDescending(e => e.Id).Take(take + 1)
            .Select(e => new EventRow(e.Id, e.Kind, e.OccurredAt, e.Truncated, e.Payload)).ToListAsync(http.RequestAborted);
        var next = rows.Count > take ? rows[take - 1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Results.Ok(new Page<EventRow>(rows.Take(take).ToList(), next));
    }

    private static async Task<IResult> Stream(Guid id, HttpContext http, MonitorDb db, Broker broker)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Sse.Stream(broker.Subscribe(Broker.Workspace(id)), [new StreamMessage("ready", new { workspaceId = id })], http.RequestAborted);
    }
}

/// <summary>A keyset cursor "ticks.guid": opaque to the client, stable under inserts.</summary>
public static class Cursor
{
    public static string Of(DateTimeOffset at, Guid id) => $"{at.UtcTicks}.{id:N}";

    public static (DateTimeOffset At, Guid Id)? Parse(string? cursor)
    {
        var parts = cursor?.Split('.');
        return parts is [var ticks, var id] && long.TryParse(ticks, out var t) && Guid.TryParseExact(id, "N", out var g)
               && t >= DateTimeOffset.MinValue.UtcTicks && t <= DateTimeOffset.MaxValue.UtcTicks
            ? (new DateTimeOffset(t, TimeSpan.Zero), g)
            : null;
    }
}
