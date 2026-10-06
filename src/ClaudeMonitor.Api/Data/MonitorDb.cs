using System.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>The central database (docs/data-model.md). Table and column names are snake_case.</summary>
public sealed class MonitorDb(DbContextOptions<MonitorDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserLogin> UserLogins => Set<UserLogin>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();
    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<WorkspaceSettings> WorkspaceSettings => Set<WorkspaceSettings>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<DeviceAuthorization> DeviceAuthorizations => Set<DeviceAuthorization>();
    public DbSet<HarnessKind> HarnessKinds => Set<HarnessKind>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<HarnessSession> HarnessSessions => Set<HarnessSession>();
    public DbSet<SessionTask> SessionTasks => Set<SessionTask>();
    public DbSet<SubagentRun> SubagentRuns => Set<SubagentRun>();
    public DbSet<SessionUsage> SessionUsage => Set<SessionUsage>();
    public DbSet<AgentBatch> AgentBatches => Set<AgentBatch>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<EventArchive> EventArchives => Set<EventArchive>();
    public DbSet<SessionCommand> SessionCommands => Set<SessionCommand>();
    public DbSet<PermissionRequest> PermissionRequests => Set<PermissionRequest>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<MachineGrant> MachineGrants => Set<MachineGrant>();
    public DbSet<MachineJob> MachineJobs => Set<MachineJob>();
    public DbSet<MachineMetric> MachineMetrics => Set<MachineMetric>();
    public DbSet<MachineAlert> MachineAlerts => Set<MachineAlert>();
    public DbSet<RemoteRun> RemoteRuns => Set<RemoteRun>();
    public DbSet<RemoteRunOutput> RemoteRunOutput => Set<RemoteRunOutput>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        MonitorDbModel.Configure(b);
        foreach (var entity in b.Model.GetEntityTypes())
        {
            entity.SetTableName(SnakeCase(entity.GetTableName()!));
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(SnakeCase(key.GetName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(SnakeCase(index.GetDatabaseName()!));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(SnakeCase(foreignKey.GetConstraintName()!));
            }
        }
    }

    /// <summary>"EmailNormalized" -> "email_normalized"; table names arrive pluralised ("SessionUsage" stays singular).</summary>
    public static string SnakeCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
