using Podium.Api.Approvals;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.AccessPolicies;

/// <summary>
/// A machine's access as its page shows it (ADR-0020 §4): every web policy of its workspace (all of them reach it), for the machine's OS, narrowed lexically by the
/// ceiling the agent reported, with the version and state the machine applied. The ceiling's paths go only to the machine's owner and the workspace's
/// owners and admins (<paramref name="privileged"/>); anyone else sees the policy's own lists, never a path that came from the machine.
/// </summary>
public static class AccessEffective
{
    private static readonly AccessOsRules Empty = new([], [], []);

    public static async Task<MachineAccessView> ViewAsync(PodiumDb db, Agent agent, string os, bool privileged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agent);
        var report = AccessReports.Read(agent.AccessReport);
        var ceiling = privileged ? report?.Ceiling : null;
        var enforced = await db.WorkspaceSettings.AsNoTracking().Where(s => s.WorkspaceId == agent.WorkspaceId).Select(s => s.EnforceAccessPolicies)
            .FirstOrDefaultAsync(ct);
        var rows = await (from p in AccessPolicyDelivery.For(db, agent.WorkspaceId)
                          join project in db.Projects.AsNoTracking() on p.ProjectId equals project.Id
                          select new { p, project.Name }).ToListAsync(ct);
        var applied = (report?.Policies ?? []).GroupBy(a => a.ProjectId).ToDictionary(g => g.Key, g => g.First());
        var policies = rows.Select(r =>
        {
            var document = AccessPolicyStore.Read(r.p);
            var section = AccessPolicyRules.Section(document, os) ?? Empty;
            var state = applied.GetValueOrDefault(r.p.ProjectId);
            return new MachineAccessPolicyView(r.p.ProjectId, r.Name, r.p.Version, state?.Version, state?.State, state?.IsSigned ?? r.p.Signature is not null,
                Allowed(section.AllowedFolders, ceiling?.AllowedFolders ?? [], os), [.. section.DeniedFolders.Union(ceiling?.DeniedFolders ?? [], StringComparer.Ordinal)],
                Applications(section.Applications, ceiling?.Applications ?? [], os), document.IncludeProjectFolder,
                document.Shell && (report?.Ceiling.Shell ?? true));
        }).ToList();
        return new MachineAccessView(ceiling, policies, agent.AccessReportAt, enforced || ApprovalRules.Strict(agent));
    }

    /// <summary>The folders both allow: an empty list on either side is no limit there; otherwise each folder of one side that lies under the other.</summary>
    public static IReadOnlyList<string> Allowed(IReadOnlyList<string> policy, IReadOnlyList<string> ceiling, string os)
    {
        if (ceiling.Count == 0) return policy;
        if (policy.Count == 0) return ceiling;
        return [.. policy.Where(p => AccessPolicyMath.IsUnderAny(p, ceiling, os))
            .Union(ceiling.Where(c => AccessPolicyMath.IsUnderAny(c, policy, os)), StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The applications both allow: an empty list on either side is no limit there; otherwise the policy's entries the ceiling also lists.</summary>
    public static IReadOnlyList<AccessApp> Applications(IReadOnlyList<AccessApp> policy, IReadOnlyList<AccessApp> ceiling, string os)
    {
        if (ceiling.Count == 0) return policy;
        if (policy.Count == 0) return ceiling;
        // The same path as the OS compares it: each is "under" the other only when they are equal.
        return [.. policy.Where(p => ceiling.Any(c => AccessPolicyMath.IsUnder(c.Path, p.Path, os) && AccessPolicyMath.IsUnder(p.Path, c.Path, os)))];
    }
}
