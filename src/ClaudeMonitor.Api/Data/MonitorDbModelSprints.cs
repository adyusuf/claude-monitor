using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, indexes, CHECKs and defaults of sprints (ADR-0011 §5).</summary>
internal static class MonitorDbModelSprints
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<Sprint>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
            // One active sprint per scope: the whole project, or one application.
            e.HasIndex(x => x.ProjectId).IsUnique().HasFilter("status = 'active' AND application_id IS NULL").HasDatabaseName("ux_sprints_active_project");
            e.HasIndex(x => x.ApplicationId).IsUnique().HasFilter("status = 'active' AND application_id IS NOT NULL").HasDatabaseName("ux_sprints_active_application");
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.ReleaseId);
            e.Property(x => x.StartDate).HasColumnType("date");
            e.Property(x => x.EndDate).HasColumnType("date");
            e.Property(x => x.Capacity).HasColumnType("numeric(10,2)");
            e.Property(x => x.Status).HasDefaultValue(SprintStatuses.Planned);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_sprints_status", MonitorDbModel.In("status", [.. SprintStatuses.All]));
                t.HasCheckConstraint("ck_sprints_dates", "start_date IS NULL OR end_date IS NULL OR end_date >= start_date");
                t.HasCheckConstraint("ck_sprints_capacity", "capacity IS NULL OR capacity >= 0");
                t.HasCheckConstraint("ck_sprints_number", "number > 0");
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SprintMember>(e =>
        {
            e.HasKey(x => new { x.SprintId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne<Sprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Issue>(e =>
        {
            e.HasIndex(x => x.SprintId);
            e.HasOne<Sprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
