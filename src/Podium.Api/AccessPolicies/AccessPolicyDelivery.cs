using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.AccessPolicies;

/// <summary>
/// What an agent receives of the access policies (ADR-0020 §4 rule 1): every policy of its workspace, tombstones included, so a machine that runs a
/// repository it never reported (a server with a clone and no session) still holds that repository's policy; any member can read them all on the web
/// anyway. Live policies come first, then tombstones, each in project order, at most <see cref="PoliciesMax"/>: an answer that holds exactly
/// <see cref="PoliciesMax"/> policies may have been cut, and the agent then applies the default to every run. Its settings and every run it is sent
/// carry only a stamp of that set, so it fetches the bodies again only when the stamp moves.
/// </summary>
public static class AccessPolicyDelivery
{
    public const int PoliciesMax = 200;

    internal static IQueryable<ProjectAccessPolicy> For(PodiumDb db, Guid workspaceId) =>
        db.ProjectAccessPolicies.AsNoTracking().Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.Cleared).ThenBy(p => p.ProjectId).Take(PoliciesMax);

    /// <summary>The stamp of the workspace's set now (<see cref="Stamp"/>); null when there is no policy.</summary>
    public static async Task<string?> StampAsync(PodiumDb db, Guid workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await For(db, workspaceId).Select(p => new { p.ProjectId, p.Version }).ToListAsync(ct);
        return Stamp(rows.Select(r => (r.ProjectId, r.Version)));
    }

    /// <summary>The policies themselves, with the same stamp; an empty stamp when there is none (the settings then carry null).</summary>
    public static async Task<AgentAccessPolicies> PoliciesAsync(PodiumDb db, Guid workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await For(db, workspaceId).ToListAsync(ct);
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
