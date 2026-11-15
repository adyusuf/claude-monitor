using System.IO.Pipelines;
using System.Text.Json;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// The parts of a user's export that can be large, read a chunk at a time after the last key seen (never the whole set at once) and flushed
/// per chunk: the commands they sent, their audit trail, and their sessions with each session's tasks and usage read once per batch.
/// </summary>
internal static class PrivacyExportPaging
{
    private const int ChunkSize = 500;
    private const int SessionBatch = 100;

    /// <summary>Writes <c>"name": [...]</c> from pages; a page gives each row with the key the next page starts after.</summary>
    public static async Task WriteArrayAsync<TKey>(Utf8JsonWriter w, PipeWriter body, string name, TKey start,
        Func<TKey, CancellationToken, Task<List<(TKey Key, object Row)>>> page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(page);
        w.WriteStartArray(name);
        var after = start;
        while (true)
        {
            var chunk = await page(after, ct);
            if (chunk.Count == 0) break;
            foreach (var (_, row) in chunk) JsonSerializer.Serialize(w, row, PrivacyEndpoints.Json);
            after = chunk[^1].Key;
            await w.FlushAsync(ct);
            await body.FlushAsync(ct);
        }

        w.WriteEndArray();
    }

    public static Task WriteCommandsAsync(Utf8JsonWriter w, PipeWriter body, PodiumDb db, Guid userId, CancellationToken ct) =>
        WriteArrayAsync(w, body, "commandsSent", Guid.Empty, async (after, token) =>
            (await db.SessionCommands.AsNoTracking().Where(c => c.CreatedBy == userId && c.Id.CompareTo(after) > 0).OrderBy(c => c.Id).Take(ChunkSize)
                .Select(c => new { c.Id, c.SessionId, c.Kind, c.Body, c.Status, c.CreatedAt }).ToListAsync(token))
            .Select(c => (c.Id, (object)c)).ToList(), ct);

    public static Task WriteAuditTrailAsync(Utf8JsonWriter w, PipeWriter body, PodiumDb db, Guid userId, CancellationToken ct) =>
        WriteArrayAsync(w, body, "auditTrail", 0L, async (after, token) =>
            (await db.AuditEvents.AsNoTracking().Where(a => a.ActorUserId == userId && a.Id > after).OrderBy(a => a.Id).Take(ChunkSize)
                .Select(a => new { a.Id, Row = new { a.Action, a.WorkspaceId, a.TargetType, a.TargetId, a.At } }).ToListAsync(token))
            .Select(a => (a.Id, (object)a.Row)).ToList(), ct);

    /// <summary>The sessions of the user's agents in start order, a batch at a time; each batch's tasks and usage are one query each.</summary>
    public static async Task WriteSessionsAsync(Utf8JsonWriter w, PipeWriter body, PodiumDb db, Guid userId, CancellationToken ct)
    {
        w.WriteStartArray("sessions");
        var seen = false;
        var lastStart = DateTimeOffset.MinValue;
        var lastId = Guid.Empty;
        while (true)
        {
            var batch = await (from s in db.HarnessSessions.AsNoTracking()
                               join a in db.Agents.AsNoTracking() on s.AgentId equals a.Id
                               where a.UserId == userId
                                     && (!seen || s.StartedAt > lastStart || (s.StartedAt == lastStart && s.Id.CompareTo(lastId) > 0))
                               orderby s.StartedAt, s.Id
                               select s).Take(SessionBatch).ToListAsync(ct);
            if (batch.Count == 0) break;
            var ids = batch.Select(s => s.Id).ToList();
            var tasks = (await db.SessionTasks.AsNoTracking().Where(t => ids.Contains(t.SessionId))
                .Select(t => new { t.SessionId, t.Subject, t.Status, t.UpdatedAt }).ToListAsync(ct)).ToLookup(t => t.SessionId);
            var usage = (await db.SessionUsage.AsNoTracking().Where(u => ids.Contains(u.SessionId))
                .Select(u => new { u.SessionId, u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CostUsd }).ToListAsync(ct))
                .ToLookup(u => u.SessionId);
            foreach (var s in batch)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("workspaceId", s.WorkspaceId);
                w.WriteString("title", s.Title);
                w.WriteString("model", s.Model);
                w.WriteString("status", s.Status);
                w.WriteString("startedAt", s.StartedAt);
                PrivacyEndpoints.Write(w, "tasks", tasks[s.Id].Select(t => new { t.Subject, t.Status, t.UpdatedAt }).ToList());
                PrivacyEndpoints.Write(w, "usage",
                    usage[s.Id].Select(u => new { u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CostUsd }).ToList());
                await WriteEventsAsync(w, body, db, s.Id, ct);
                w.WriteEndObject();
            }

            seen = true;
            lastStart = batch[^1].StartedAt;
            lastId = batch[^1].Id;
            await w.FlushAsync(ct);
            await body.FlushAsync(ct);
        }

        w.WriteEndArray();
    }

    private static async Task WriteEventsAsync(Utf8JsonWriter w, PipeWriter body, PodiumDb db, Guid sessionId, CancellationToken ct)
    {
        w.WriteStartArray("events");
        long after = 0;
        while (true)
        {
            var chunk = await db.SessionEvents.AsNoTracking().Where(e => e.SessionId == sessionId && e.Id > after)
                .OrderBy(e => e.Id).Take(ChunkSize).ToListAsync(ct);
            if (chunk.Count == 0) break;
            foreach (var e in chunk)
            {
                JsonSerializer.Serialize(w, new { e.Kind, e.OccurredAt, e.Truncated, Payload = e.Payload.RootElement }, PrivacyEndpoints.Json);
                e.Payload.Dispose();
            }

            after = chunk[^1].Id;
            await w.FlushAsync(ct);
            await body.FlushAsync(ct);
        }

        w.WriteEndArray();
    }
}
