using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Projects;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.AccessPolicies;

/// <summary>
/// Saving a project's access policy (ADR-0020 §4 rules 1-3). Only the workspace's owners and admins, re-authenticated; the document valid and already
/// canonical (the API never rewrites a body a signature may cover); every repo key one of the project's; a signature, when sent, checked against the
/// caller's registered key for the next version. A save is a compare-and-set on the version (<c>access_policy_changed</c>); a key held by another
/// live policy is refused (<c>access_key_taken</c>), one held by a cleared policy passes to this one. Audited without paths.
/// </summary>
public static class AccessPolicySaves
{
    public const string InvalidVersion = "invalid_version";
    private const string ProjectIndex = "ix_project_access_policies_project_id";
    private const string KeyIndex = "ix_project_access_policy_keys_workspace_id_project_key";

    public static async Task<IResult> SaveAsync(Guid projectId, AccessPolicySave? req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, projectId, ct);
        if (grant is null) return Http.NotFound();
        var workspaceId = grant.Project.WorkspaceId;
        if (await Access.MemberAsync(db, userId, workspaceId, Roles.Admin, ct) is null) return Results.Forbid();
        if (req is null || req.ExpectedVersion < 0) return Http.Invalid("expectedVersion", InvalidVersion);
        if (req.Document is not { } document) return Http.Invalid("document", AccessPolicyErrors.Malformed);
        if (AccessPolicyRules.Validate(document) is { } invalid) return Http.Invalid("document", invalid);
        if (!AccessPolicyRules.IsCanonical(document)) return Http.Invalid("document", AccessPolicyErrors.NotCanonical);

        var row = await db.ProjectAccessPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);
        var candidates = await AccessPolicyStore.CandidateKeysAsync(db, projectId, await AccessPolicyStore.HeldKeysAsync(db, row?.Id, ct), ct);
        if (!document.ProjectKeys.All(k => candidates.Contains(k, StringComparer.Ordinal))) return Http.Invalid("document", AccessPolicyErrors.Keys);
        if ((row?.Version ?? 0) != req.ExpectedVersion) return RemoteChecks.Conflict(AccessPolicyErrors.Changed);
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code)) return RemoteChecks.ReauthRefused();

        var version = req.ExpectedVersion + 1;
        var hash = AccessPolicySigning.Hash(document);
        string? keyId = null, signature = null;
        if (req.Signature is { Length: > 0 } sig)
        {
            if (!await VerifiesAsync(db, userId, req.KeyId, sig, workspaceId, projectId, version, hash, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ApprovalErrors.Invalid);
            }

            (keyId, signature) = (req.KeyId, sig);
        }

        var keys = document.ProjectKeys.ToList();
        var holders = await (from k in db.ProjectAccessPolicyKeys.AsNoTracking()
                             join p in db.ProjectAccessPolicies.AsNoTracking() on k.PolicyId equals p.Id
                             where k.WorkspaceId == workspaceId && keys.Contains(k.ProjectKey) && p.ProjectId != projectId
                             select new { k.PolicyId, k.ProjectKey, p.Cleared }).ToListAsync(ct);
        if (holders.Any(h => !h.Cleared)) return RemoteChecks.Conflict(AccessPolicyErrors.KeyTaken);

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var policyId = row?.Id ?? Guid.CreateVersion7();
            if (row is null)
            {
                db.ProjectAccessPolicies.Add(new ProjectAccessPolicy
                {
                    Id = policyId,
                    WorkspaceId = workspaceId,
                    ProjectId = projectId,
                    Version = version,
                    Document = AccessPolicyStore.Write(document),
                    DocumentHash = hash,
                    Cleared = document.Cleared,
                    SignedKeyId = keyId,
                    Signature = signature,
                    SignedBy = signature is null ? null : userId,
                    UpdatedBy = userId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else if (!await UpdateAsync(db, row.Id, req.ExpectedVersion, document, hash, keyId, signature, userId, now, ct))
            {
                await tx.RollbackAsync(ct);
                return RemoteChecks.Conflict(AccessPolicyErrors.Changed);
            }

            // A cleared policy keeps its keys so its tombstone still reaches the machines; a new policy for one of them takes the key over.
            var released = holders.Select(h => h.ProjectKey).ToList();
            if (released.Count > 0)
            {
                await db.ProjectAccessPolicyKeys.Where(k => k.WorkspaceId == workspaceId && released.Contains(k.ProjectKey)).ExecuteDeleteAsync(ct);
            }

            await db.ProjectAccessPolicyKeys.Where(k => k.PolicyId == policyId).ExecuteDeleteAsync(ct);
            db.ProjectAccessPolicyKeys.AddRange(keys.Select(k => new ProjectAccessPolicyKey { PolicyId = policyId, ProjectKey = k, WorkspaceId = workspaceId }));
            Audit.Add(db, http, clock, document.Cleared ? AuditActions.AccessPolicyCleared : AuditActions.AccessPolicyUpdated, workspaceId, userId,
                targetType: "project", targetId: projectId, detail: Detail(projectId, version, signature is not null, document, released.Count));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique
            && unique.ConstraintName is ProjectIndex or KeyIndex)
        {
            // Another save came first: the first policy of this project (its unique project id) or a key another policy took meanwhile.
            await tx.RollbackAsync(ct);
            return RemoteChecks.Conflict(unique.ConstraintName == ProjectIndex ? AccessPolicyErrors.Changed : AccessPolicyErrors.KeyTaken);
        }

        return Results.Ok(await AccessPolicyStore.ViewAsync(db, grant.Project, true, ct));
    }

    /// <summary>The next version in one compare-and-set: false when the version moved on since the caller read it.</summary>
    private static async Task<bool> UpdateAsync(PodiumDb db, Guid id, int expected, AccessPolicyDocument document, string hash, string? keyId,
        string? signature, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var json = AccessPolicyStore.Write(document);
        Guid? signedBy = signature is null ? null : userId;
        return 1 == await db.ProjectAccessPolicies.Where(p => p.Id == id && p.Version == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Version, expected + 1).SetProperty(p => p.Document, json).SetProperty(p => p.DocumentHash, hash)
                .SetProperty(p => p.Cleared, document.Cleared).SetProperty(p => p.SignedKeyId, keyId).SetProperty(p => p.Signature, signature)
                .SetProperty(p => p.SignedBy, signedBy).SetProperty(p => p.UpdatedBy, userId).SetProperty(p => p.UpdatedAt, now), ct);
    }

    /// <summary>True when the signature signs this version and hash under one of the caller's registered keys that is not retired.</summary>
    private static async Task<bool> VerifiesAsync(PodiumDb db, Guid userId, string? keyId, string signature, Guid workspaceId, Guid projectId, int version,
        string hash, CancellationToken ct)
    {
        if (keyId is not { Length: > 0 and <= PodiumDbModelAccess.KeyIdMax } || signature.Length > PodiumDbModelAccess.SignatureMax) return false;
        var registered = await db.ApprovalKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.Fingerprint == keyId && k.RetiredAt == null).Select(k => k.PublicKey).FirstOrDefaultAsync(ct);
        var key = ApprovalSigning.FromBase64(registered);
        var bytes = ApprovalSigning.FromBase64(signature);
        return key is not null && bytes is not null && AccessPolicySigning.Verify(key, workspaceId, projectId, version, hash, bytes);
    }

    /// <summary>What the audit row keeps: counts and flags, never a path or an executable.</summary>
    private static object Detail(Guid projectId, int version, bool signed, AccessPolicyDocument d, int released)
    {
        AccessOsRules[] sections = [d.Linux, d.MacOs, d.Windows];
        return new
        {
            projectId,
            version,
            signed,
            keys = d.ProjectKeys.Count,
            allowedFolders = sections.Sum(s => s.AllowedFolders.Count),
            deniedFolders = sections.Sum(s => s.DeniedFolders.Count),
            applications = sections.Sum(s => s.Applications.Count),
            includeProjectFolder = d.IncludeProjectFolder,
            shell = d.Shell,
            keysTakenOver = released,
        };
    }
}
