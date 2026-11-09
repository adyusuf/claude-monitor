using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Data;

/// <summary>Keys, CHECKs and column types of project access policies and the access columns elsewhere (ADR-0020 §4; docs/data-model.md §7).</summary>
internal static class PodiumDbModelAccess
{
    public const int KeyIdMax = 64;
    public const int SignatureMax = 128;

    public static void Configure(ModelBuilder b)
    {
        b.Entity<ProjectAccessPolicy>(e =>
        {
            e.HasIndex(x => x.ProjectId).IsUnique();
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Document).HasColumnType("jsonb");
            e.Property(x => x.DocumentHash).HasMaxLength(AccessPolicyRules.Sha256HexLength);
            e.Property(x => x.Cleared).HasDefaultValue(false);
            e.Property(x => x.SignedKeyId).HasMaxLength(KeyIdMax);
            e.Property(x => x.Signature).HasMaxLength(SignatureMax);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_project_access_policies_version", "version >= 1");
                t.HasCheckConstraint("ck_project_access_policies_signed", "(signed_key_id IS NULL) = (signature IS NULL)");
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SignedBy).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ProjectAccessPolicyKey>(e =>
        {
            e.HasKey(x => new { x.PolicyId, x.ProjectKey });
            e.HasIndex(x => new { x.WorkspaceId, x.ProjectKey }).IsUnique();
            e.Property(x => x.ProjectKey).HasMaxLength(AccessPolicyRules.MaxProjectKeyLength);
            e.HasOne<ProjectAccessPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        });

        // The access report an agent sends with its profile (ceiling and the state of each policy); NULL: an older agent.
        b.Entity<Agent>(e => e.Property(x => x.AccessReport).HasColumnType("jsonb"));

        b.Entity<PermissionRequest>(e =>
        {
            e.Property(x => x.AccessScope).HasMaxLength(16);
            e.ToTable(t => t.HasCheckConstraint("ck_permission_requests_access_scope",
                "access_scope IS NULL OR " + PodiumDbModel.In("access_scope", [.. AccessScopes.All])));
        });

        b.Entity<WorkspaceSettings>(e => e.Property(x => x.EnforceAccessPolicies).HasDefaultValue(false));
    }
}
