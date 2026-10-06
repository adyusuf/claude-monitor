using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, indexes and CHECK constraints (docs/data-model.md). Names are mapped to snake_case afterwards.</summary>
internal static class MonitorDbModel
{
    private static string In(string column, params string[] values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    public static void Configure(ModelBuilder b)
    {
        ForeignKeys(b);
        b.Entity<HarnessKind>().HasData(new HarnessKind
        {
            Code = "claude_code",
            DisplayName = "Claude Code",
            AddedAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.EmailNormalized).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_users_status",
                In("status", UserStatuses.Active, UserStatuses.Disabled, UserStatuses.Deleted)));
        });
        b.Entity<UserLogin>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ProviderSubject }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("ck_user_logins_provider", In("provider", Providers.GitHub, Providers.Google)));
        });
        b.Entity<UserToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_user_tokens_purpose",
                In("purpose", TokenPurposes.VerifyEmail, TokenPurposes.ResetPassword, TokenPurposes.MfaPending)));
        });
        b.Entity<UserRecoveryCode>(e =>
        {
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });
        b.Entity<LoginSession>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });
        b.Entity<Workspace>(e =>
            e.ToTable(t => t.HasCheckConstraint("ck_workspaces_status", In("status", "active", "archived"))));
        b.Entity<WorkspaceMember>(e =>
        {
            e.HasKey(x => new { x.WorkspaceId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("ck_workspace_members_role", In("role", [.. Roles.All])));
        });
        b.Entity<WorkspaceInvitation>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.WorkspaceId);
            e.ToTable(t => t.HasCheckConstraint("ck_workspace_invitations_role", In("role", [.. Roles.All])));
        });
        b.Entity<WorkspaceSettings>(e =>
        {
            e.HasKey(x => x.WorkspaceId);
            e.Property(x => x.AgentUpdate).HasDefaultValue(UpdateModes.Off);
            e.Property(x => x.ClaudeUpdate).HasDefaultValue(false);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_workspace_settings_retention", "retention_days BETWEEN 1 AND 3650");
                t.HasCheckConstraint("ck_workspace_settings_agent_update", In("agent_update", [.. UpdateModes.All]));
            });
        });
        b.Entity<Machine>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.MachineKeyHash }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_machines_os", In("os", "macos", "windows")));
        });
        b.Entity<Agent>(e =>
        {
            e.HasIndex(x => new { x.MachineId, x.UserId }).IsUnique().HasFilter("status = 'active'");
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.UserId);
            e.ToTable(t => t.HasCheckConstraint("ck_agents_status", In("status", AgentStatuses.Active, AgentStatuses.Revoked)));
        });
        b.Entity<AgentToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.AgentId);
            e.ToTable(t => t.HasCheckConstraint("ck_agent_tokens_kind", In("kind", AgentTokenKinds.Access, AgentTokenKinds.Refresh)));
        });
        b.Entity<DeviceAuthorization>(e =>
        {
            e.HasIndex(x => x.DeviceCodeHash).IsUnique();
            e.HasIndex(x => x.UserCode).IsUnique().HasFilter("status = 'pending'");
            e.ToTable(t => t.HasCheckConstraint("ck_device_authorizations_status", In("status", DeviceStatuses.Pending,
                DeviceStatuses.Approved, DeviceStatuses.Denied, DeviceStatuses.Expired, DeviceStatuses.Consumed)));
        });
        b.Entity<HarnessKind>(e => e.HasKey(x => x.Code));
        b.Entity<Project>(e => e.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique());
        b.Entity<HarnessSession>(e =>
        {
            e.HasIndex(x => new { x.AgentId, x.HarnessKind, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.LastEventAt, x.Id }).IsDescending(false, true, true);
            e.HasIndex(x => new { x.ProjectId, x.LastEventAt }).IsDescending(false, true);
            e.ToTable(t => t.HasCheckConstraint("ck_harness_sessions_status", In("status", SessionStatuses.Active,
                SessionStatuses.Idle, SessionStatuses.Waiting, SessionStatuses.Ended)));
        });
        b.Entity<SessionTask>(e =>
        {
            e.HasIndex(x => new { x.SessionId, x.ExternalId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_session_tasks_status", In("status", "pending", "in_progress", "completed", "deleted")));
        });
        b.Entity<SubagentRun>(e =>
        {
            e.HasIndex(x => new { x.SessionId, x.ExternalId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_subagent_runs_status", In("status", "running", "finished", "failed")));
        });
        b.Entity<SessionUsage>(e =>
        {
            e.ToTable("session_usage");
            e.HasKey(x => new { x.SessionId, x.Model });
            e.Property(x => x.CostUsd).HasPrecision(12, 6);
        });
        b.Entity<AgentBatch>(e => e.HasKey(x => new { x.AgentId, x.BatchSeq }));
        b.Entity<SessionEvent>(e =>
        {
            e.HasKey(x => new { x.Id, x.ReceivedAt });
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasIndex(x => new { x.SessionId, x.OccurredAt });
            e.HasIndex(x => new { x.WorkspaceId, x.ReceivedAt });
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });
        b.Entity<EventArchive>(e => e.HasIndex(x => new { x.WorkspaceId, x.Day }));
        b.Entity<SessionCommand>(e =>
        {
            e.HasIndex(x => new { x.AgentId, x.Status }).HasFilter("status IN ('queued', 'delivered')");
            e.HasIndex(x => new { x.SessionId, x.CreatedAt }).IsDescending(false, true);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_session_commands_kind", In("kind", "prompt", "stop"));
                t.HasCheckConstraint("ck_session_commands_status",
                    In("status", "queued", "delivered", "applied", "failed", "expired", "cancelled"));
            });
        });
        b.Entity<PermissionRequest>(e =>
        {
            e.HasIndex(x => new { x.SessionId, x.Status });
            e.Property(x => x.ToolInput).HasColumnType("jsonb");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_permission_requests_status", In("status",
                    PermissionStatuses.Open, PermissionStatuses.Answered, PermissionStatuses.Expired));
                t.HasCheckConstraint("ck_permission_requests_decision", "decision IS NULL OR decision IN ('allow', 'deny')");
            });
        });
        b.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasIndex(x => new { x.WorkspaceId, x.At }).IsDescending(false, true);
            e.HasIndex(x => new { x.ActorUserId, x.At }).IsDescending(false, true);
            e.Property(x => x.Detail).HasColumnType("jsonb");
        });
    }

    /// <summary>Every reference is a foreign key; nothing cascades (rows are retired, never deleted, global #4).</summary>
    private static void ForeignKeys(ModelBuilder b)
    {
        void Fk<TChild, TParent>(System.Linq.Expressions.Expression<Func<TChild, object?>> key)
            where TChild : class
            where TParent : class =>
            b.Entity<TChild>().HasOne<TParent>().WithMany().HasForeignKey(key).OnDelete(DeleteBehavior.Restrict);

        Fk<UserLogin, User>(x => x.UserId);
        Fk<UserToken, User>(x => x.UserId);
        Fk<UserRecoveryCode, User>(x => x.UserId);
        Fk<LoginSession, User>(x => x.UserId);
        Fk<Workspace, User>(x => x.CreatedBy);
        Fk<WorkspaceMember, Workspace>(x => x.WorkspaceId);
        Fk<WorkspaceMember, User>(x => x.UserId);
        Fk<WorkspaceInvitation, Workspace>(x => x.WorkspaceId);
        Fk<WorkspaceInvitation, User>(x => x.InvitedBy);
        Fk<WorkspaceSettings, Workspace>(x => x.WorkspaceId);
        Fk<Machine, Workspace>(x => x.WorkspaceId);
        Fk<Agent, Machine>(x => x.MachineId);
        Fk<Agent, User>(x => x.UserId);
        Fk<Agent, Workspace>(x => x.WorkspaceId);
        Fk<AgentToken, Agent>(x => x.AgentId);
        Fk<Project, Workspace>(x => x.WorkspaceId);
        Fk<HarnessSession, Workspace>(x => x.WorkspaceId);
        Fk<HarnessSession, Agent>(x => x.AgentId);
        Fk<HarnessSession, Project>(x => x.ProjectId);
        Fk<HarnessSession, HarnessKind>(x => x.HarnessKind);
        Fk<SessionTask, HarnessSession>(x => x.SessionId);
        Fk<SubagentRun, HarnessSession>(x => x.SessionId);
        Fk<SessionUsage, HarnessSession>(x => x.SessionId);
        Fk<AgentBatch, Agent>(x => x.AgentId);
        Fk<SessionEvent, HarnessSession>(x => x.SessionId);
        Fk<EventArchive, Workspace>(x => x.WorkspaceId);
        Fk<SessionCommand, HarnessSession>(x => x.SessionId);
        Fk<SessionCommand, User>(x => x.CreatedBy);
        Fk<PermissionRequest, HarnessSession>(x => x.SessionId);
    }
}
