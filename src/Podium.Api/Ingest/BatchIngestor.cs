using System.Text;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Streaming;
using Podium.Api.Text;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Ingest;

public enum IngestOutcome
{
    Stored,
    Duplicate,
    TooLarge,
}

/// <summary>
/// Stores one batch from an agent in one transaction: the batch number first (a number already stored means a retry,
/// and nothing else is written), then each event and its projections. An event that is not valid (or holds text PostgreSQL cannot store) is
/// skipped and counted, never guessed at; an event larger than the workspace's limit is stored as a truncation marker. An event of a session
/// recorded in another workspace than the agent's current one (the agent was moved since, F-244) is skipped too: nothing is written across workspaces.
/// </summary>
public sealed class BatchIngestor(PodiumDb db, ApiConfig config, Broker broker, TimeProvider clock)
{
    public const int KindMax = 100;
    public const int ExternalIdMax = 200;
    public const int ProjectKeyMax = 300;
    public const int PreviewChars = 2000;

    public async Task<(IngestOutcome Outcome, int Stored)> IngestAsync(Guid agentId, Guid workspaceId, EventBatch batch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Events is null || batch.Events.Count > config.MaxBatchEvents)
        {
            return (IngestOutcome.TooLarge, 0);
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO agent_batches (agent_id, batch_seq, event_count, bytes, received_at) VALUES ({agentId}, {batch.BatchSeq}, {batch.Events.Count}, 0, {now}) ON CONFLICT DO NOTHING",
            ct);
        if (inserted == 0)
        {
            return (IngestOutcome.Duplicate, 0);
        }

        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == workspaceId, ct);
        var harnesses = await db.HarnessKinds.AsNoTracking().Select(h => h.Code).ToListAsync(ct);
        var projector = new EventProjector(db, config);
        var sessions = new Dictionary<(string, string), HarnessSession?>();
        var stored = 0;
        long bytes = 0;
        foreach (var captured in batch.Events)
        {
            if (!Valid(captured, harnesses))
            {
                continue;
            }

            // The agent's clock may run ahead: an event dated beyond the allowed skew counts as now, so one such event cannot pin the session at the top of the list.
            var e = captured.OccurredAt > now + config.ClockSkewMax ? captured with { OccurredAt = now } : captured;

            // A NUL or half a surrogate pair anywhere in it would fail the whole batch in PostgreSQL: the event is skipped instead.
            var raw = e.Payload.GetRawText();
            if (!StorableText.Json(raw))
            {
                continue;
            }

            if (await SessionAsync(sessions, agentId, workspaceId, e, ct) is not { } session)
            {
                continue;
            }

            var size = Encoding.UTF8.GetByteCount(raw);
            bytes += size;
            var truncated = e.Truncated || size > settings.EventMaxBytes;
            db.SessionEvents.Add(new SessionEvent
            {
                SessionId = session.Id,
                WorkspaceId = workspaceId,
                Kind = e.Kind,
                OccurredAt = e.OccurredAt,
                ReceivedAt = now,
                Payload = size > settings.EventMaxBytes ? Marker(raw, size) : JsonDocument.Parse(raw),
                Truncated = truncated,
            });
            await projector.ApplyAsync(session, e, ct);
            stored++;
        }

        await db.SaveChangesAsync(ct);
        await db.AgentBatches.Where(b => b.AgentId == agentId && b.BatchSeq == batch.BatchSeq)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Bytes, bytes).SetProperty(b => b.EventCount, stored), ct);
        await tx.CommitAsync(ct);

        foreach (var s in sessions.Values.OfType<HarnessSession>())
        {
            broker.Publish(Broker.Workspace(workspaceId), new StreamMessage("session", new { sessionId = s.Id, status = s.Status }));
        }

        return (IngestOutcome.Stored, stored);
    }

    private static bool Valid(CapturedEvent e, List<string> harnesses) =>
        e is not null
        && harnesses.Contains(e.HarnessKind)
        && e.Kind is { Length: > 0 and <= KindMax }
        && e.SessionExternalId is { Length: > 0 and <= ExternalIdMax }
        && e.Payload.ValueKind == JsonValueKind.Object
        && (e.ProjectKey is null || e.ProjectKey.Length <= ProjectKeyMax)
        && StorableText.Plain(e.Kind) && StorableText.Plain(e.SessionExternalId) && StorableText.Plain(e.ProjectKey)
        && StorableText.Plain(e.ProjectName) && StorableText.Plain(e.GitBranch);

    private static JsonDocument Marker(string raw, int size) =>
        JsonSerializer.SerializeToDocument(new { truncated = true, bytes = size, preview = raw[..Math.Min(raw.Length, PreviewChars)] });

    /// <summary>The event's session, created on its first event; null when it was recorded in another workspace than <paramref name="workspaceId"/>.</summary>
    private async Task<HarnessSession?> SessionAsync(Dictionary<(string, string), HarnessSession?> cache, Guid agentId,
        Guid workspaceId, CapturedEvent e, CancellationToken ct)
    {
        var key = (e.HarnessKind, e.SessionExternalId);
        if (cache.TryGetValue(key, out var session))
        {
            return session;
        }

        session = await db.HarnessSessions.FirstOrDefaultAsync(
            s => s.AgentId == agentId && s.HarnessKind == e.HarnessKind && s.ExternalId == e.SessionExternalId, ct);
        if (session is not null && session.WorkspaceId != workspaceId)
        {
            // The agent moved to another workspace after this session began: it stays where it was recorded, unchanged (F-244).
            cache[key] = null;
            return null;
        }

        if (session is null)
        {
            session = new HarnessSession
            {
                WorkspaceId = workspaceId,
                AgentId = agentId,
                HarnessKind = e.HarnessKind,
                ExternalId = e.SessionExternalId,
                StartedAt = e.OccurredAt,
                LastEventAt = e.OccurredAt,
            };
            db.HarnessSessions.Add(session);
        }

        if (e.ProjectKey is { Length: > 0 } projectKey)
        {
            session.RepoId = await RepoAsync(workspaceId, projectKey, e.ProjectName ?? projectKey, ct);
            await MachineProjects.SeenInSessionAsync(db, agentId, workspaceId, projectKey, e.ProjectName ?? projectKey, e.GitBranch, e.OccurredAt, ct);
        }

        session.GitBranch = e.GitBranch ?? session.GitBranch;
        cache[key] = session;
        return session;
    }

    private async Task<Guid> RepoAsync(Guid workspaceId, string key, string name, CancellationToken ct)
    {
        var repo = db.Repos.Local.FirstOrDefault(p => p.WorkspaceId == workspaceId && p.Key == key)
                      ?? await db.Repos.FirstOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Key == key, ct);
        if (repo is null)
        {
            var display = name.Length > 200 ? name[..200] : name;
            repo = new Repo
            {
                WorkspaceId = workspaceId,
                Key = key,
                DisplayName = display,
                DisplayNameSearch = SearchText.Normalize(display),
                CreatedAt = clock.GetUtcNow(),
            };
            db.Repos.Add(repo);
        }

        return repo.Id;
    }
}
