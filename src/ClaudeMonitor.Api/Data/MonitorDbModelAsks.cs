using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Data;

/// <summary>Keys, CHECKs and indexes of machine asks (ADR-0008 §4; docs/data-model.md §3b). The question and the answer are NULL once an account deletion emptied them.</summary>
internal static class MonitorDbModelAsks
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<Agent>(e => e.HasOne<User>().WithMany().HasForeignKey(x => x.AcceptsAsksSetBy).OnDelete(DeleteBehavior.Restrict));
        b.Entity<MachineAsk>(e =>
        {
            e.HasIndex(x => new { x.RequesterAgentId, x.ClientKey }).IsUnique();
            e.HasIndex(x => new { x.TargetAgentId, x.Status }).HasFilter("status IN ('queued', 'delivered')");
            e.HasIndex(x => new { x.RequesterAgentId, x.CreatedAt });
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt }).IsDescending(false, true);
            e.Property(x => x.ClientKey).HasMaxLength(100);
            e.Property(x => x.ProjectKey).HasMaxLength(RunKinds.ProjectKeyMax);
            e.Property(x => x.TargetSession).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(16).HasDefaultValue(AskStatuses.Queued);
            e.Property(x => x.Error).HasMaxLength(100);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_machine_asks_status", MonitorDbModel.In("status", [.. AskStatuses.All]));
                t.HasCheckConstraint("ck_machine_asks_same_user", "requester_user_id = target_user_id");
                t.HasCheckConstraint("ck_machine_asks_two_agents", "requester_agent_id <> target_agent_id");
                t.HasCheckConstraint("ck_machine_asks_question", "question IS NULL OR char_length(question) BETWEEN 1 AND 8000");
                t.HasCheckConstraint("ck_machine_asks_answer", "answer IS NULL OR char_length(answer) <= 100000");
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.RequesterAgentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.TargetAgentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<HarnessSession>().WithMany().HasForeignKey(x => x.RequesterSessionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
