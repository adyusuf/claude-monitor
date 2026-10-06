using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, indexes, CHECKs and defaults of remote work (docs/data-model.md §3b). New columns carry database defaults.</summary>
internal static class MonitorDbModelRemote
{
    private static string In(string column, IEnumerable<string> values) => MonitorDbModel.In(column, [.. values]);

    private const string Live = "status IN ('approved', 'delivered', 'running')";

    public static void Configure(ModelBuilder b)
    {
        Metrics(b);
        Runs(b);
        Grants(b);
        Jobs(b);
    }

    private static void Metrics(ModelBuilder b)
    {
        b.Entity<Agent>(e =>
        {
            e.Property(x => x.ExecLevel).HasDefaultValue(ExecLevels.Off);
            e.Property(x => x.ServiceMode).HasDefaultValue(false);
            e.ToTable(t => t.HasCheckConstraint("ck_agents_exec_level", In("exec_level", ExecLevels.All)));
        });
        b.Entity<WorkspaceSettings>(e =>
        {
            e.Property(x => x.AlertCpuPct).HasDefaultValue(90);
            e.Property(x => x.AlertMemoryPct).HasDefaultValue(90);
            e.Property(x => x.AlertDiskPct).HasDefaultValue(90);
            e.Property(x => x.AlertSustainSeconds).HasDefaultValue(300);
            e.Property(x => x.RemoteRunsEnabled).HasDefaultValue(false);
            e.ToTable(t => t.HasCheckConstraint("ck_workspace_settings_alerts",
                "alert_cpu_pct BETWEEN 1 AND 100 AND alert_memory_pct BETWEEN 1 AND 100 AND alert_disk_pct BETWEEN 1 AND 100 "
                + "AND alert_sustain_seconds BETWEEN 60 AND 86400"));
        });
        b.Entity<MachineMetric>(e =>
        {
            e.HasKey(x => new { x.AgentId, x.SampledAt });
            e.HasIndex(x => x.SampledAt).HasMethod("brin");
            e.Property(x => x.Disks).HasColumnType("jsonb");
            e.ToTable(t => t.HasCheckConstraint("ck_machine_metrics_cpu", "cpu_pct BETWEEN 0 AND 100"));
        });
        b.Entity<MachineAlert>(e =>
        {
            e.HasIndex(x => new { x.AgentId, x.Kind, x.Subject }).IsUnique().HasFilter("state = 'open'");
            e.HasIndex(x => new { x.WorkspaceId, x.OpenedAt, x.Id }).IsDescending(false, true, true);
            e.Property(x => x.Subject).HasDefaultValue("");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_machine_alerts_kind", In("kind", AlertKinds.All));
                t.HasCheckConstraint("ck_machine_alerts_state", In("state", AlertStates.All));
            });
        });
        Fk<MachineMetric, Agent>(b, x => x.AgentId);
        Fk<MachineMetric, Workspace>(b, x => x.WorkspaceId);
        Fk<MachineAlert, Agent>(b, x => x.AgentId);
        Fk<MachineAlert, Workspace>(b, x => x.WorkspaceId);
    }

    private static void Runs(ModelBuilder b)
    {
        b.Entity<RemoteRun>(e =>
        {
            e.HasIndex(x => new { x.RequesterAgentId, x.ClientKey }).IsUnique();
            e.HasIndex(x => new { x.TargetAgentId, x.Status }).HasFilter(Live);
            e.HasIndex(x => new { x.TargetUserId, x.CreatedAt }).HasFilter("status = 'pending_approval'");
            e.HasIndex(x => new { x.TargetAgentId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt }).IsDescending(false, true);
            e.Property(x => x.Status).HasDefaultValue(RunStatuses.PendingApproval);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_remote_runs_mode", In("mode", RunModes.All));
                t.HasCheckConstraint("ck_remote_runs_status", In("status", RunStatuses.All));
                t.HasCheckConstraint("ck_remote_runs_mode_shape",
                    "(mode = 'argv' AND shell_command IS NULL) OR (mode = 'shell' AND argv IS NULL)");
                t.HasCheckConstraint("ck_remote_runs_timeout", "timeout_seconds BETWEEN 1 AND 3600");
                t.HasCheckConstraint("ck_remote_runs_client_key", "char_length(client_key) BETWEEN 1 AND 100");
            });
        });
        b.Entity<RemoteRunOutput>(e =>
        {
            e.HasKey(x => new { x.RunId, x.Seq });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_remote_run_output_stream", In("stream", RunStreams.All));
                t.HasCheckConstraint("ck_remote_run_output_seq", "seq >= 0");
                t.HasCheckConstraint("ck_remote_run_output_bytes", "bytes BETWEEN 1 AND 65536");
            });
        });
        Fk<RemoteRun, Workspace>(b, x => x.WorkspaceId);
        Fk<RemoteRun, Agent>(b, x => x.RequesterAgentId);
        Fk<RemoteRun, Agent>(b, x => x.TargetAgentId);
        Fk<RemoteRun, User>(b, x => x.RequesterUserId);
        Fk<RemoteRun, User>(b, x => x.TargetUserId);
        Fk<RemoteRun, User>(b, x => x.DecidedBy);
        Fk<RemoteRun, HarnessSession>(b, x => x.RequesterSessionId);
        Fk<RemoteRunOutput, RemoteRun>(b, x => x.RunId);
    }

    private static void Grants(ModelBuilder b)
    {
        b.Entity<MachineGrant>(e =>
        {
            e.HasIndex(x => new { x.TargetAgentId, x.GranteeUserId }).HasFilter("status = 'active'");
            e.HasIndex(x => new { x.TargetAgentId, x.GranteeUserId, x.TemplateHash }).IsUnique()
                .HasFilter("status IN ('requested', 'active')");
            e.HasIndex(x => x.OwnerUserId).HasFilter("status = 'requested'");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_machine_grants_status", In("status", GrantStatuses.All));
                t.HasCheckConstraint("ck_machine_grants_expiry", "expires_at <= created_at + interval '90 days'");
                t.HasCheckConstraint("ck_machine_grants_timeout", "max_timeout_seconds BETWEEN 1 AND 3600");
            });
        });
        Fk<MachineGrant, Workspace>(b, x => x.WorkspaceId);
        Fk<MachineGrant, Agent>(b, x => x.TargetAgentId);
        Fk<MachineGrant, Agent>(b, x => x.GranteeAgentId);
        Fk<MachineGrant, Agent>(b, x => x.RequestedByAgentId);
        Fk<MachineGrant, User>(b, x => x.OwnerUserId);
        Fk<MachineGrant, User>(b, x => x.GranteeUserId);
        Fk<MachineGrant, User>(b, x => x.DecidedBy);
        Fk<MachineGrant, User>(b, x => x.RevokedBy);
        Fk<RemoteRun, MachineGrant>(b, x => x.GrantId);
    }

    private static void Jobs(ModelBuilder b)
    {
        b.Entity<MachineJob>(e =>
        {
            e.HasIndex(x => new { x.TargetAgentId, x.NameSearch }).IsUnique().HasFilter("status IN ('proposed', 'active')");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_machine_jobs_status", In("status", JobStatuses.All));
                t.HasCheckConstraint("ck_machine_jobs_timeout", "timeout_seconds BETWEEN 1 AND 3600");
            });
        });
        Fk<MachineJob, Workspace>(b, x => x.WorkspaceId);
        Fk<MachineJob, Agent>(b, x => x.TargetAgentId);
        Fk<MachineJob, Agent>(b, x => x.ProposedByAgentId);
        Fk<MachineJob, User>(b, x => x.OwnerUserId);
        Fk<MachineJob, User>(b, x => x.ProposedByUserId);
        Fk<MachineJob, User>(b, x => x.DecidedBy);
        Fk<RemoteRun, MachineJob>(b, x => x.JobId);
    }

    private static void Fk<TChild, TParent>(ModelBuilder b, System.Linq.Expressions.Expression<Func<TChild, object?>> key)
        where TChild : class
        where TParent : class =>
        b.Entity<TChild>().HasOne<TParent>().WithMany().HasForeignKey(key).OnDelete(DeleteBehavior.Restrict);
}
