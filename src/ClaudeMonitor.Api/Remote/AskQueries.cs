using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>Reads of asks (ADR-0008 §3f). The question and the answer go only to the user the ask belongs to (requester and target are the same user).</summary>
public static class AskQueries
{
    public static async Task<AskView?> ForUserAsync(MonitorDb db, Guid id, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await (from a in db.MachineAsks.AsNoTracking()
                         join t in db.Agents.AsNoTracking() on a.TargetAgentId equals t.Id
                         join m in db.Machines.AsNoTracking() on t.MachineId equals m.Id
                         where a.Id == id && a.RequesterUserId == userId
                         select new { a, m.Hostname }).FirstOrDefaultAsync(ct);
        return row is null ? null : View(row.a, row.Hostname);
    }

    /// <summary>The asks of one target machine for the web, newest first; only the owner of the machine (who is also the asker) sees any.</summary>
    public static async Task<List<AskView>> ForTargetAsync(MonitorDb db, Guid targetAgentId, Guid userId, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from a in db.MachineAsks.AsNoTracking()
                          join t in db.Agents.AsNoTracking() on a.TargetAgentId equals t.Id
                          join m in db.Machines.AsNoTracking() on t.MachineId equals m.Id
                          where a.TargetAgentId == targetAgentId && a.TargetUserId == userId
                          orderby a.CreatedAt descending, a.Id descending
                          select new { a, m.Hostname }).Take(limit).ToListAsync(ct);
        return rows.Select(r => View(r.a, r.Hostname)).ToList();
    }

    /// <summary>The queued asks a target's daemon still owes an answer for, replayed on every stream connection.</summary>
    public static async Task<List<AskMessage>> WaitingForAsync(MonitorDb db, Guid targetAgentId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from a in db.MachineAsks.AsNoTracking()
                          join r in db.Agents.AsNoTracking() on a.RequesterAgentId equals r.Id
                          join m in db.Machines.AsNoTracking() on r.MachineId equals m.Id
                          where a.TargetAgentId == targetAgentId && a.Status == AskStatuses.Queued && a.ExpiresAt > now && a.Question != null
                          orderby a.CreatedAt
                          select new { a.Id, a.Question, a.ProjectKey, a.ExpiresAt, m.Hostname }).Take(20).ToListAsync(ct);
        return rows.Select(x => new AskMessage(x.Id, x.Question!, AskCreator.Label(x.Hostname), x.ProjectKey, x.ExpiresAt)).ToList();
    }

    public static AskView View(MachineAsk a, string hostname)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new AskView(a.Id, a.TargetAgentId, hostname, a.Status, a.Question, a.Answer, a.AnswerTruncated, a.Error, a.CreatedAt, a.ExpiresAt, a.DeliveredAt, a.AnsweredAt);
    }
}
