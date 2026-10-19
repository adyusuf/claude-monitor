using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, indexes, CHECKs and defaults of the agent actions (ADR-0005, "Agent actions from the web").</summary>
internal static class MonitorDbModelActions
{
    private const string Open = "status IN ('queued', 'delivered')";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<AgentAction>(e =>
        {
            // One open action of a kind per agent: a second request returns the open one.
            e.HasIndex(x => new { x.AgentId, x.Kind }).IsUnique().HasFilter(Open);
            e.HasIndex(x => new { x.AgentId, x.CreatedAt, x.Id }).IsDescending(false, true, true);
            e.Property(x => x.Status).HasDefaultValue(AgentActionStatuses.Queued);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_agent_actions_kind", MonitorDbModel.In("kind", [.. AgentActionKinds.All]));
                t.HasCheckConstraint("ck_agent_actions_status", MonitorDbModel.In("status", [.. AgentActionStatuses.All]));
            });
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Agent>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AcceptsClaudeControlSetBy).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_agents_claude_auto_mode", "claude_auto_mode IS NULL OR " + MonitorDbModel.In("claude_auto_mode", [.. ClaudeAutoModes.All])));
        });
    }
}
