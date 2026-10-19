using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The detail of one machine (ADR-0008 §3a′), for the web's machine page and the agent's <c>monitor_machine</c> tool. The caller has
/// already been checked as a workspace member. Listening services go only to the machine's owner and to admins, and only while the
/// owner has them switched on; the `claude` path stays out of the tools, as in the list.
/// </summary>
public static class MachineDetails
{
    public const int ProjectsMax = 50;
    public const int RunsPerKind = 3;
    public const int RecentRunsScanned = 200;
    public static readonly TimeSpan ErrorWindow = TimeSpan.FromDays(7);

    public static async Task<MachineDetail?> GetAsync(MonitorDb db, ApiConfig config, Guid workspaceId, Guid agentId, Guid callerUserId,
        bool callerIsAdmin, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        var machine = (await MachineQueries.MachinesAsync(db, config, workspaceId, now, ct)).FirstOrDefault(m => m.AgentId == agentId);
        if (machine is null) return null;
        var agent = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == agentId, ct);
        var servicesAllowed = agent.ShareServices && (callerIsAdmin || agent.UserId == callerUserId);
        return new MachineDetail(machine, agent.AdminNote, agent.AdminNoteSetAt, agent.ShareServices,
            servicesAllowed ? MachineFacts.ServicesFrom(agent.Services) : null, servicesAllowed ? agent.ServicesAt : null,
            await ProjectsAsync(db, workspaceId, agentId, now, ct));
    }

    private static async Task<IReadOnlyList<MachineProjectView>> ProjectsAsync(MonitorDb db, Guid workspaceId, Guid agentId, DateTimeOffset now,
        CancellationToken ct)
    {
        var rows = await db.MachineProjects.AsNoTracking().Where(p => p.AgentId == agentId)
            .OrderByDescending(p => p.LastSeenAt).ThenBy(p => p.ProjectKey).Take(ProjectsMax).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var keys = rows.Select(r => r.ProjectKey).ToList();
        var runs = await db.RemoteRuns.AsNoTracking()
            .Where(r => r.TargetAgentId == agentId && r.ProjectKey != null && keys.Contains(r.ProjectKey))
            .OrderByDescending(r => r.CreatedAt).Take(RecentRunsScanned)
            .Select(r => new { r.ProjectKey, r.Kind, r.Status, r.ExitCode, r.CreatedAt, r.EndedAt, r.OutputTruncated, r.TestsPassed, r.TestsFailed, r.TestsSkipped })
            .ToListAsync(ct);
        var since = now - ErrorWindow;
        var errors = await (from e in db.SessionEvents.AsNoTracking()
                            join s in db.HarnessSessions.AsNoTracking() on e.SessionId equals s.Id
                            join p in db.Projects.AsNoTracking() on s.ProjectId equals p.Id
                            where s.AgentId == agentId && s.WorkspaceId == workspaceId && e.Kind == EventKinds.HookPrefix + "PostToolUseFailure" && e.OccurredAt > since
                            group e by p.Key into g
                            select new { Key = g.Key, Count = g.Count() }).ToListAsync(ct);
        return rows.Select(r => new MachineProjectView(r.ProjectKey, r.Name, r.Branch, r.HeadSha, r.Tag, r.LastSeenAt, r.Source,
            runs.Where(x => x.ProjectKey == r.ProjectKey).GroupBy(x => x.Kind).SelectMany(g => g.Take(RunsPerKind))
                .Select(x => new ProjectRunSummary(x.Kind, x.Status, x.ExitCode, x.EndedAt ?? x.CreatedAt, x.OutputTruncated, x.TestsPassed, x.TestsFailed, x.TestsSkipped))
                .OrderByDescending(x => x.At).ToList(),
            errors.FirstOrDefault(e => e.Key == r.ProjectKey)?.Count ?? 0, r.ClaudeTrusted)).ToList();
    }
}
