using System.Text.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>
/// The tie between approval rules and access policies (ADR-0019, ADR-0020 §4): for a path tool a rule is made or matched only when the agent found the
/// path inside the session's project access layers (access_scope = inside; an older agent sends none, so no rule), the path has no "." or ".." and
/// no empty segment (the check is lexical, so /a/x/../secret must not pass as under /a), and, when a live web policy covers the session's repository,
/// the machine's access report shows that very version applied and the path is under none of the denied folders of the machine OS's section and
/// under one of its allowed folders (or the policy allows the project's own folder, which the agent's "inside" already judged). Bash and searches
/// without a path are not affected. Policies and machine OSes are read once per instance, so a page of requests asks the database once for each.
/// </summary>
public sealed class ApprovalRuleAccess(PodiumDb db)
{
    private readonly Dictionary<(Guid Workspace, Guid Repo), ProjectAccessPolicy?> policies = [];
    private readonly Dictionary<Guid, string?> oses = [];

    public async Task<bool> AllowsAsync(Agent agent, Guid? repoId, string toolName, JsonElement input, string? accessScope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!ApprovalRules.IsPathCall(toolName, input, out var path)) return true;
        if (path is null || accessScope != AccessScopes.Inside || !PlainSegments(path)) return false;
        if (repoId is not { } repo || await PolicyAsync(agent.WorkspaceId, repo, ct) is not { } row) return true;
        if (!Applied(agent, row)) return false; // the machine has not applied what the web holds: its "inside" was judged against something else
        var document = AccessPolicyStore.Read(row);
        if (await OsAsync(agent.MachineId, ct) is not { } os || AccessPolicyRules.Section(document, os) is not { } section) return false;
        var lexical = os == OsKinds.Windows ? path.Replace('/', '\\') : path;
        return !AccessPolicyMath.IsUnderAny(lexical, section.DeniedFolders, os)
            && (document.IncludeProjectFolder || AccessPolicyMath.IsUnderAny(lexical, section.AllowedFolders, os));
    }

    /// <summary>
    /// True when no segment is ".", ".." or empty; only the root of an absolute Unix path ("/x") may start with a separator, and none may end with one.
    /// </summary>
    public static bool PlainSegments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/', '\\');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or ".." || (parts[i].Length == 0 && (i > 0 || parts.Length == 1))) return false;
        }

        return true;
    }

    /// <summary>True when the agent's last access report shows this policy's current version applied (not kept from before, not ignored).</summary>
    private static bool Applied(Agent agent, ProjectAccessPolicy row) =>
        AccessReports.Read(agent.AccessReport)?.Policies is { } reported
        && reported.Any(p => p.ProjectId == row.ProjectId && p.Version == row.Version && p.State == AccessPolicyStates.Applied);

    /// <summary>The live (not cleared) policy that covers the repository's key in the workspace (keys compared as RepoKeys.Same does), or null.</summary>
    private async Task<ProjectAccessPolicy?> PolicyAsync(Guid workspaceId, Guid repoId, CancellationToken ct)
    {
        if (policies.TryGetValue((workspaceId, repoId), out var known)) return known;
        var key = await db.Repos.AsNoTracking().Where(r => r.Id == repoId && r.WorkspaceId == workspaceId).Select(r => r.Key).FirstOrDefaultAsync(ct);
        var row = key is null ? null : await AccessPolicyStore.LiveForKeyAsync(db, workspaceId, key, ct);
        policies[(workspaceId, repoId)] = row;
        return row;
    }

    private async Task<string?> OsAsync(Guid machineId, CancellationToken ct)
    {
        if (oses.TryGetValue(machineId, out var known)) return known;
        var os = await db.Machines.AsNoTracking().Where(m => m.Id == machineId).Select(m => m.Os).FirstOrDefaultAsync(ct);
        oses[machineId] = os;
        return os;
    }
}
