using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, CHECKs and column types of machine roles, presence and the tool inventory (ADR-0008 §4; docs/data-model.md §3). All nullable: NULL is "unset" or "unknown".</summary>
internal static class MonitorDbModelMachines
{
    private static string Optional(string column, IEnumerable<string> values) => $"{column} IS NULL OR " + MonitorDbModel.In(column, [.. values]);

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Agent>(e =>
        {
            e.Property(x => x.Interpreters).HasColumnType("jsonb");
            e.Property(x => x.Tools).HasColumnType("jsonb");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RoleSetBy).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_agents_role_environment", Optional("role_environment", MachineEnvironments.All));
                t.HasCheckConstraint("ck_agents_role_function", Optional("role_function", MachineFunctions.All));
                t.HasCheckConstraint("ck_agents_claimed_environment", Optional("claimed_environment", MachineEnvironments.All));
                t.HasCheckConstraint("ck_agents_claimed_function", Optional("claimed_function", MachineFunctions.All));
                t.HasCheckConstraint("ck_agents_claude_kind", Optional("claude_kind", ClaudeKinds.All));
                t.HasCheckConstraint("ck_agents_claude_sessions", "(claude_sessions_live IS NULL OR claude_sessions_live >= 0) AND (claude_sessions_idle IS NULL OR claude_sessions_idle >= 0)");
            });
        });
    }
}
