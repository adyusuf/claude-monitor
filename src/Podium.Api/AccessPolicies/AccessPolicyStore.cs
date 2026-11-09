using System.Text;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.AccessPolicies;

/// <summary>
/// Reading project access policies (ADR-0020 §4): the stored document, the repo keys a policy may cover (those of the project's active
/// applications and those it already holds), and the view the project page shows. The caller has already checked that the user may see the project.
/// </summary>
public static class AccessPolicyStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored document. It was valid and canonical when saved; a row the API cannot read is a fault, never "no limit".</summary>
    public static AccessPolicyDocument Read(ProjectAccessPolicy row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.Deserialize<AccessPolicyDocument>(row.Document, Json)
            ?? throw new InvalidOperationException($"access policy {row.Id} has no document");
    }

    public static string Write(AccessPolicyDocument document) => JsonSerializer.Serialize(document, Json);

    /// <summary>The keys the policy itself holds now (none for a project without a policy).</summary>
    public static async Task<List<string>> HeldKeysAsync(PodiumDb db, Guid? policyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return policyId is { } id
            ? await db.ProjectAccessPolicyKeys.AsNoTracking().Where(k => k.PolicyId == id).Select(k => k.ProjectKey).ToListAsync(ct)
            : [];
    }

    /// <summary>
    /// The repo keys a policy of this project may cover: the key of each active application's repository URL (<see cref="RepoKeys.FromRemote"/>),
    /// and the keys the policy already holds (an application whose URL changed does not take its repository out of the policy). NFC, ordinal order,
    /// one spelling per repository as <see cref="RepoKeys.Same"/> compares them: the one the policy holds, else the first in ordinal order.
    /// </summary>
    public static async Task<List<string>> CandidateKeysAsync(PodiumDb db, Guid projectId, IEnumerable<string> held, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var urls = await db.Applications.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Status == ManagedStatuses.Active && a.RepoUrl != null).Select(a => a.RepoUrl).ToListAsync(ct);
        return held.Concat(urls.Select(RepoKeys.FromRemote).OfType<string>().Order(StringComparer.Ordinal))
            .Select(k => k.Normalize(NormalizationForm.FormC)).Where(AccessPolicyRules.IsValidKey)
            .Distinct(RepoKeys.Comparer).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The live (not cleared) policy of the workspace that covers the repository, keys compared as <see cref="RepoKeys.Same"/> does; null when none
    /// does. A workspace holds at most a few thousand keys, so they are read and compared here.
    /// </summary>
    public static async Task<ProjectAccessPolicy?> LiveForKeyAsync(PodiumDb db, Guid workspaceId, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var keys = await (from k in db.ProjectAccessPolicyKeys.AsNoTracking()
                          join p in db.ProjectAccessPolicies.AsNoTracking() on k.PolicyId equals p.Id
                          where k.WorkspaceId == workspaceId && p.WorkspaceId == workspaceId && !p.Cleared
                          select new { k.PolicyId, k.ProjectKey }).ToListAsync(ct);
        return keys.FirstOrDefault(k => RepoKeys.Same(k.ProjectKey, key)) is { } hit
            ? await db.ProjectAccessPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == hit.PolicyId, ct)
            : null;
    }

    public const int ReportedKeysScanned = 5000;

    /// <summary>
    /// Keys machines of the workspace report that name one of the candidates' repositories but are spelled differently (case or Unicode form), so
    /// the page can say a machine's clone URL differs from the application's. They match the policy all the same (<see cref="RepoKeys.Same"/>).
    /// </summary>
    public static async Task<List<string>> NearKeysAsync(PodiumDb db, Guid workspaceId, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (candidates.Count == 0) return [];
        var reported = await db.MachineProjects.AsNoTracking().Where(p => p.WorkspaceId == workspaceId).Select(p => p.ProjectKey).Distinct()
            .OrderBy(k => k).Take(ReportedKeysScanned).ToListAsync(ct);
        return reported.Where(k => !candidates.Contains(k, StringComparer.Ordinal) && candidates.Contains(k, RepoKeys.Comparer))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The project page's answer: the <see cref="AccessPolicyView"/> and, added to it, <c>nearKeys</c> (<see cref="NearKeysAsync"/>).</summary>
    public static async Task<System.Text.Json.Nodes.JsonObject> PageAsync(PodiumDb db, ManagedProject project, bool canEdit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var view = await ViewAsync(db, project, canEdit, ct);
        var page = JsonSerializer.SerializeToNode(view, Json)!.AsObject();
        page["nearKeys"] = JsonSerializer.SerializeToNode(await NearKeysAsync(db, project.WorkspaceId, view.CandidateKeys, ct), Json);
        return page;
    }

    /// <summary>
    /// Active machines of the workspace that require signed approvals: every one of them receives the policy (<see cref="AccessPolicyDelivery"/>)
    /// and ignores an unsigned version of it.
    /// </summary>
    public static async Task<int> MachinesRequiringSignatureAsync(PodiumDb db, Guid workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Agents.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active && a.ApprovalsRequired).CountAsync(ct);
    }

    /// <summary>
    /// The project page's view: the saved policy, or version 0 with the default document over the candidate keys when none was saved. The signer's
    /// name is shown when the version is signed; CanEdit is the caller's right to save (workspace owner or admin), decided by the caller.
    /// </summary>
    public static async Task<AccessPolicyView> ViewAsync(PodiumDb db, ManagedProject project, bool canEdit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(project);
        var row = await db.ProjectAccessPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == project.Id, ct);
        var candidates = await CandidateKeysAsync(db, project.Id, await HeldKeysAsync(db, row?.Id, ct), ct);
        if (row is null)
        {
            var fallback = AccessPolicyRules.Canonical(AccessPolicyRules.DefaultDocument(candidates));
            return new AccessPolicyView(project.Id, 0, fallback, AccessPolicySigning.Hash(fallback), true, false, null, candidates,
                await MachinesRequiringSignatureAsync(db, project.WorkspaceId, ct), canEdit);
        }

        var document = Read(row);
        var signer = row is { Signature: not null, SignedBy: { } by }
            ? await db.Users.AsNoTracking().Where(u => u.Id == by).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        return new AccessPolicyView(project.Id, row.Version, document, row.DocumentHash, false, row.Signature is not null, signer, candidates,
            await MachinesRequiringSignatureAsync(db, project.WorkspaceId, ct), canEdit);
    }
}
