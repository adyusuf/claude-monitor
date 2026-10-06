using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// A job as the web shows it. Reason is what the proposing Claude wrote (untrusted), shown only to the owner and the
/// proposer. CanDecide: the owner may approve or deny it. CanRetire: the owner may retire it.
/// </summary>
public sealed record WebJobView(Guid Id, Guid TargetAgentId, Guid OwnerUserId, string Name, IReadOnlyList<string> Argv, string Cwd,
    int TimeoutSeconds, string? Reason, string Status, Guid ProposedByUserId, string ProposedByName, Guid? ProposedByAgentId,
    string? ProposedByHostname, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, DateTimeOffset? RetiredAt, bool CanDecide,
    bool CanRetire);

public static class JobQueries
{
    public const int AgentListMax = 100;

    public static JobView View(MachineJob j)
    {
        ArgumentNullException.ThrowIfNull(j);
        return new JobView(j.Id, j.TargetAgentId, j.Name, j.Argv ?? [], j.Cwd ?? "", j.TimeoutSeconds, j.Status, j.CreatedAt);
    }

    /// <summary>The active and proposed jobs of a workspace, optionally one target's, newest first.</summary>
    public static async Task<List<JobView>> ForAgentAsync(MonitorDb db, Guid workspaceId, Guid? targetAgentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.MachineJobs.AsNoTracking()
            .Where(j => j.WorkspaceId == workspaceId && (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed)
                        && (targetAgentId == null || j.TargetAgentId == targetAgentId))
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(AgentListMax).ToListAsync(ct);
        return rows.Select(View).ToList();
    }

    /// <summary>The jobs of one target for the web, newest first, key-paged.</summary>
    public static async Task<List<WebJobView>> ForWebAsync(MonitorDb db, Guid targetAgentId, Guid userId, Guid? jobId,
        DateTimeOffset? before, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from j in db.MachineJobs.AsNoTracking()
                          join u in db.Users.AsNoTracking() on j.ProposedByUserId equals u.Id
                          where j.TargetAgentId == targetAgentId
                                && (jobId == null || j.Id == jobId)
                                && (before == null || j.CreatedAt < before)
                          orderby j.CreatedAt descending, j.Id descending
                          select new
                          {
                              j,
                              Proposer = u.DisplayName,
                              Host = (from pa in db.Agents join pm in db.Machines on pa.MachineId equals pm.Id
                                      where pa.Id == j.ProposedByAgentId select pm.Hostname).FirstOrDefault(),
                          }).Take(limit).ToListAsync(ct);
        return rows.Select(x =>
        {
            var j = x.j;
            var owner = j.OwnerUserId == userId;
            var reasonVisible = owner || j.ProposedByUserId == userId;
            return new WebJobView(j.Id, j.TargetAgentId, j.OwnerUserId, j.Name, j.Argv ?? [], j.Cwd ?? "", j.TimeoutSeconds,
                reasonVisible ? j.Reason : null, j.Status, j.ProposedByUserId, x.Proposer, j.ProposedByAgentId, x.Host, j.CreatedAt,
                j.DecidedAt, j.RetiredAt, CanDecide: owner && j.Status == JobStatuses.Proposed,
                CanRetire: owner && (j.Status == JobStatuses.Proposed || j.Status == JobStatuses.Active));
        }).ToList();
    }
}
