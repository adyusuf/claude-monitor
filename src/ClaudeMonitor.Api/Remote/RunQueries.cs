using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// A run as the web shows it. Command and output only for the requester and the target's owner (Visible); everyone else
/// in the workspace sees who, where and when. Hash is what an approval must send back; Interpreter asks for the red
/// notice; RecentOutput says the requester read another machine's output in the last minutes (a hint of injection).
/// </summary>
public sealed record WebRunView(Guid Id, Guid TargetAgentId, string TargetHostname, string TargetUser, Guid RequesterAgentId,
    string RequesterHostname, string RequesterUser, Guid? RequesterSessionId, bool Visible, string Mode, IReadOnlyList<string>? Argv,
    string? ShellCommand, string? Cwd, int TimeoutSeconds, string? Reason, string Status, int? ExitCode, string? Error,
    long OutputBytes, bool OutputTruncated, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt,
    Guid? GrantId, Guid? JobId, bool CanDecide, bool SelfApproval, string? Hash, bool Interpreter, bool RecentOutput);

public static class RunQueries
{
    public static readonly TimeSpan RecentOutputWindow = TimeSpan.FromMinutes(10);

    public static async Task<RunView?> ForRequesterAsync(MonitorDb db, Guid id, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await (from r in db.RemoteRuns.AsNoTracking()
                         join a in db.Agents.AsNoTracking() on r.TargetAgentId equals a.Id
                         join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                         where r.Id == id && r.RequesterUserId == userId
                         select new { r, m.Hostname }).FirstOrDefaultAsync(ct);
        return row is null ? null : View(row.r, row.Hostname);
    }

    public static RunView View(RemoteRun r, string hostname)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new RunView(r.Id, r.TargetAgentId, hostname, r.Mode, r.Argv, r.ShellCommand, r.Cwd, r.TimeoutSeconds, r.Status,
            r.ExitCode, r.Error, r.OutputBytes, r.OutputTruncated, r.CreatedAt, r.ExpiresAt, r.EndedAt, r.GrantId, r.JobId);
    }

    public static async Task<RunOutputPage> OutputAsync(MonitorDb db, Guid runId, int after, int limit, bool done, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var chunks = await db.RemoteRunOutput.AsNoTracking()
            .Where(o => o.RunId == runId && o.Seq > after).OrderBy(o => o.Seq).Take(limit)
            .Select(o => new RunOutputChunk(o.Seq, o.Stream, o.Body, o.GapBefore)).ToListAsync(ct);
        return new RunOutputPage(chunks, chunks.Count > 0 ? chunks[^1].Seq : after, done && chunks.Count < limit);
    }

    /// <summary>Runs of a workspace for the web, newest first, key-paged; optionally one target agent's.</summary>
    public static async Task<List<WebRunView>> ForWebAsync(MonitorDb db, ApiConfig config, Guid workspaceId, Guid userId,
        Guid? targetAgentId, Guid? runId, DateTimeOffset? before, int limit, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from r in db.RemoteRuns.AsNoTracking()
                          join ta in db.Agents.AsNoTracking() on r.TargetAgentId equals ta.Id
                          join tm in db.Machines.AsNoTracking() on ta.MachineId equals tm.Id
                          join tu in db.Users.AsNoTracking() on r.TargetUserId equals tu.Id
                          join ra in db.Agents.AsNoTracking() on r.RequesterAgentId equals ra.Id
                          join rm in db.Machines.AsNoTracking() on ra.MachineId equals rm.Id
                          join ru in db.Users.AsNoTracking() on r.RequesterUserId equals ru.Id
                          where r.WorkspaceId == workspaceId
                                && (targetAgentId == null || r.TargetAgentId == targetAgentId)
                                && (runId == null || r.Id == runId)
                                && (before == null || r.CreatedAt < before)
                          orderby r.CreatedAt descending, r.Id descending
                          select new { r, Target = tm.Hostname, TargetOs = tm.Os, TargetUser = tu.DisplayName, Requester = rm.Hostname, RequesterUser = ru.DisplayName })
            .Take(limit).ToListAsync(ct);
        var requesters = rows.Where(x => x.r.Status == RunStatuses.PendingApproval).Select(x => x.r.RequesterAgentId).Distinct().ToList();
        var since = now - RecentOutputWindow;
        var readRecently = await db.RemoteRuns.AsNoTracking()
            .Where(r => requesters.Contains(r.RequesterAgentId) && r.EndedAt > since && r.OutputBytes > 0)
            .Select(r => r.RequesterAgentId).Distinct().ToListAsync(ct);
        return rows.Select(x =>
        {
            var r = x.r;
            var visible = r.RequesterUserId == userId || r.TargetUserId == userId;
            var pending = r.Status == RunStatuses.PendingApproval;
            return new WebRunView(r.Id, r.TargetAgentId, x.Target, x.TargetUser, r.RequesterAgentId, x.Requester, x.RequesterUser,
                r.RequesterSessionId, visible, r.Mode, visible ? r.Argv : null, visible ? r.ShellCommand : null, visible ? r.Cwd : null,
                r.TimeoutSeconds, visible ? r.Reason : null, r.Status, r.ExitCode, visible ? r.Error : null, r.OutputBytes,
                r.OutputTruncated, r.CreatedAt, r.ExpiresAt, r.EndedAt, r.GrantId, r.JobId,
                CanDecide: pending && r.TargetUserId == userId, SelfApproval: r.TargetUserId == r.RequesterUserId,
                Hash: pending && r.TargetUserId == userId ? RunCreator.RunHash(r) : null,
                Interpreter: r.Mode == RunModes.Shell || (r.Argv is { Count: > 0 } argv && GrantMatcher.IsInterpreter(argv[0], x.TargetOs)),
                RecentOutput: pending && readRecently.Contains(r.RequesterAgentId));
        }).ToList();
    }
}
