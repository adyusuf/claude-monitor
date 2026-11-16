using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Remote;

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
    public static async Task<List<JobView>> ForAgentAsync(PodiumDb db, Guid workspaceId, Guid? targetAgentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.MachineJobs.AsNoTracking()
            .Where(j => j.WorkspaceId == workspaceId && (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed)
                        && (targetAgentId == null || j.TargetAgentId == targetAgentId))
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(AgentListMax).ToListAsync(ct);
        return rows.Select(View).ToList();
    }

    /// <summary>
    /// The statuses a list filter names: one status of <see cref="JobStatuses.All"/>, the groups <c>open</c> and <c>finished</c>, or several of them separated
    /// by commas. False for an unknown name; empty or null is no filter (null statuses).
    /// </summary>
    public static bool TryStatuses(string? filter, out IReadOnlyList<string>? statuses)
    {
        statuses = null;
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var named = new List<string>();
        foreach (var part in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case JobStatuses.OpenGroup:
                    named.AddRange(JobStatuses.Open);
                    break;
                case JobStatuses.FinishedGroup:
                    named.AddRange(JobStatuses.Finished);
                    break;
                default:
                    if (!JobStatuses.All.Contains(part)) return false;
                    named.Add(part);
                    break;
            }
        }

        statuses = named.Count == 0 ? null : named.Distinct().ToList();
        return true;
    }

    /// <summary>The jobs of one target for the web, newest first, key-paged; <paramref name="statuses"/> (null: all) keeps only jobs in those statuses.</summary>
    public static async Task<List<WebJobView>> ForWebAsync(PodiumDb db, Guid targetAgentId, Guid userId, Guid? jobId,
        DateTimeOffset? before, int limit, CancellationToken ct, IReadOnlyList<string>? statuses = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var wanted = statuses?.ToArray();
        var rows = await (from j in db.MachineJobs.AsNoTracking()
                          join u in db.Users.AsNoTracking() on j.ProposedByUserId equals u.Id
                          where j.TargetAgentId == targetAgentId
                                && (jobId == null || j.Id == jobId)
                                && (before == null || j.CreatedAt < before)
                                && (wanted == null || wanted.Contains(j.Status))
                          orderby j.CreatedAt descending, j.Id descending
                          select new
                          {
                              j,
                              Proposer = u.DisplayName,
                              Host = (from pa in db.Agents
                                      join pm in db.Machines on pa.MachineId equals pm.Id
                                      where pa.Id == j.ProposedByAgentId
                                      select pm.Hostname).FirstOrDefault(),
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
