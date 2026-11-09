using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.AccessPolicies;

/// <summary>
/// What an agent receives of the access policies (ADR-0020 §4 rule 1): the policies whose repo keys meet the repositories its machine reports
/// (machine_projects), tombstones included, at most <see cref="PoliciesMax"/>. Its settings and every run it is sent carry only a stamp of that set,
/// so it fetches the bodies again only when the stamp moves.
/// </summary>
public static class AccessPolicyDelivery
{
    public const int PoliciesMax = 200;

    internal static IQueryable<ProjectAccessPolicy> For(PodiumDb db, Guid agentId, Guid workspaceId) =>
        db.ProjectAccessPolicies.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && db.ProjectAccessPolicyKeys.Any(k => k.PolicyId == p.Id
                && db.MachineProjects.Any(m => m.AgentId == agentId && m.WorkspaceId == workspaceId && m.ProjectKey == k.ProjectKey)))
            .OrderBy(p => p.ProjectId).Take(PoliciesMax);

    /// <summary>The stamp of the agent's set now (<see cref="Stamp"/>); null when no policy reaches it.</summary>
    public static async Task<string?> StampAsync(PodiumDb db, Guid agentId, Guid workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await For(db, agentId, workspaceId).Select(p => new { p.ProjectId, p.Version }).ToListAsync(ct);
        return Stamp(rows.Select(r => (r.ProjectId, r.Version)));
    }

    /// <summary>The policies themselves, with the same stamp; an empty stamp when there is none (the settings then carry null).</summary>
    public static async Task<AgentAccessPolicies> PoliciesAsync(PodiumDb db, Guid agentId, Guid workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await For(db, agentId, workspaceId).ToListAsync(ct);
        var messages = rows.Select(r => new AccessPolicyMessage(r.ProjectId, r.WorkspaceId, r.Version, AccessPolicyStore.Read(r), r.DocumentHash,
            r.SignedKeyId, r.Signature)).ToList();
        return new AgentAccessPolicies(Stamp(rows.Select(r => (r.ProjectId, r.Version))) ?? "", messages);
    }

    /// <summary>Lowercase hex SHA-256 over the "projectId:version" lines in ordinal order, joined by a newline; null for no policy.</summary>
    public static string? Stamp(IEnumerable<(Guid ProjectId, int Version)> policies)
    {
        var lines = policies.Select(p => $"{p.ProjectId:D}:{p.Version.ToString(CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }
}
