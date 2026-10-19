using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, indexes, CHECKs and defaults of managed projects and applications (ADR-0011).</summary>
internal static class MonitorDbModelProjects
{
    private const string Active = "status = 'active'";

    private static string Array(string column, IEnumerable<string> allowed) =>
        $"{column} <@ ARRAY[{string.Join(", ", allowed.Select(v => $"'{v}'"))}]::text[]";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<ManagedProject>(e =>
        {
            e.ToTable("projects");
            e.HasIndex(x => new { x.WorkspaceId, x.NameSearch }).IsUnique().HasFilter(Active);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.Stakeholders).HasColumnType("jsonb");
            e.Property(x => x.BudgetAmount).HasColumnType("numeric(14,2)");
            e.Property(x => x.StartDate).HasColumnType("date");
            e.Property(x => x.EndDate).HasColumnType("date");
            e.Property(x => x.Status).HasDefaultValue(ManagedStatuses.Active);
            e.Property(x => x.PriorityScale).HasDefaultValue(PriorityScales.P0ToP3);
            e.Property(x => x.EstimationUnit).HasDefaultValue(EstimationUnits.StoryPoints);
            e.Property(x => x.SprintLengthDays).HasDefaultValue(14);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_projects_status", MonitorDbModel.In("status", [.. ManagedStatuses.All]));
                t.HasCheckConstraint("ck_projects_priority_scale", MonitorDbModel.In("priority_scale", [.. PriorityScales.All]));
                t.HasCheckConstraint("ck_projects_estimation_unit", MonitorDbModel.In("estimation_unit", [.. EstimationUnits.All]));
                t.HasCheckConstraint("ck_projects_sprint_length", "sprint_length_days BETWEEN 1 AND 42");
                t.HasCheckConstraint("ck_projects_item_types", $"cardinality(item_types) >= 1 AND {Array("item_types", WorkItemTypes.All)}");
                t.HasCheckConstraint("ck_projects_dates", "end_date IS NULL OR start_date IS NULL OR end_date >= start_date");
                t.HasCheckConstraint("ck_projects_budget", "(budget_amount IS NULL) = (budget_currency IS NULL) AND (budget_amount IS NULL OR budget_amount >= 0)");
                t.HasCheckConstraint("ck_projects_stakeholders", "stakeholders IS NULL OR (jsonb_typeof(stakeholders) = 'array' AND jsonb_array_length(stakeholders) <= 50)");
                t.HasCheckConstraint("ck_projects_checklists", "cardinality(definition_of_ready) <= 30 AND cardinality(definition_of_done) <= 30");
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectMember>(e =>
        {
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("ck_project_members_role", MonitorDbModel.In("role", [.. ProjectRoles.All])));
            e.HasOne<ManagedProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AddedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ManagedApplication>(e =>
        {
            e.ToTable("applications");
            e.HasIndex(x => new { x.ProjectId, x.NameSearch }).IsUnique().HasFilter(Active);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.TechOwnerUserId);
            e.Property(x => x.Environments).HasColumnType("jsonb");
            e.Property(x => x.NonFunctionalTargets).HasColumnType("jsonb");
            e.Property(x => x.Status).HasDefaultValue(ManagedStatuses.Active);
            e.Property(x => x.Kind).HasDefaultValue(ApplicationKinds.Web);
            e.Property(x => x.VersioningScheme).HasDefaultValue(VersioningSchemes.SemVer);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_applications_status", MonitorDbModel.In("status", [.. ManagedStatuses.All]));
                t.HasCheckConstraint("ck_applications_kind", MonitorDbModel.In("kind", [.. ApplicationKinds.All]));
                t.HasCheckConstraint("ck_applications_versioning_scheme", MonitorDbModel.In("versioning_scheme", [.. VersioningSchemes.All]));
                t.HasCheckConstraint("ck_applications_environments", "environments IS NULL OR (jsonb_typeof(environments) = 'array' AND jsonb_array_length(environments) <= 3)");
                t.HasCheckConstraint("ck_applications_targets", "non_functional_targets IS NULL OR (jsonb_typeof(non_functional_targets) = 'array' AND jsonb_array_length(non_functional_targets) <= 20)");
                t.HasCheckConstraint("ck_applications_tech_stack", "cardinality(tech_stack) <= 30");
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TechOwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ApplicationComponent>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.NameSearch }).IsUnique().HasFilter(Active);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Status).HasDefaultValue(ManagedStatuses.Active);
            e.Property(x => x.Kind).HasDefaultValue(ComponentKinds.Component);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_application_components_status", MonitorDbModel.In("status", [.. ManagedStatuses.All]));
                t.HasCheckConstraint("ck_application_components_kind", MonitorDbModel.In("kind", [.. ComponentKinds.All]));
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ApplicationRelease>(e =>
        {
            e.HasIndex(x => new { x.ApplicationId, x.NameSearch }).IsUnique().HasFilter("status <> 'cancelled'");
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.PlannedDate).HasColumnType("date");
            e.Property(x => x.Status).HasDefaultValue(ReleaseStatuses.Planned);
            e.ToTable(t => t.HasCheckConstraint("ck_application_releases_status", MonitorDbModel.In("status", [.. ReleaseStatuses.All])));
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
