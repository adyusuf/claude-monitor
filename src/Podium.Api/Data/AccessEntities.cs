namespace Podium.Api.Data;

// The access policy of a managed project (ADR-0020 §4; docs/data-model.md §7).

/// <summary>
/// A project's access policy: one row per managed project, never deleted. Every save is a new version; clearing is a version with
/// <see cref="Cleared"/> set (a tombstone the agents still receive). The document is stored exactly as the browser sent and signed it, in its
/// canonical form (<see cref="Podium.Contracts.AccessPolicyRules"/>), with its hash; the signature is the owner's browser key over version and hash.
/// </summary>
public sealed class ProjectAccessPolicy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Version { get; set; }

    /// <summary>jsonb: the <see cref="Podium.Contracts.AccessPolicyDocument"/> as saved.</summary>
    public string Document { get; set; } = "{}";

    /// <summary>Lowercase hex SHA-256 of the document (<see cref="Podium.Contracts.AccessPolicySigning.Hash"/>).</summary>
    public string DocumentHash { get; set; } = "";
    public bool Cleared { get; set; }

    /// <summary>The fingerprint of the approval key that signed this version, and its signature (base64); both null when unsigned.</summary>
    public string? SignedKeyId { get; set; }
    public string? Signature { get; set; }
    public Guid? SignedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A repo key a policy covers; one key belongs to at most one policy in a workspace, so editing an application's URL cannot move it.</summary>
public sealed class ProjectAccessPolicyKey
{
    public Guid PolicyId { get; set; }
    public string ProjectKey { get; set; } = "";
    public Guid WorkspaceId { get; set; }
}
