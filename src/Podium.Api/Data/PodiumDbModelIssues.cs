using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Data;

/// <summary>Keys, indexes, CHECKs and defaults of issues and their links, test cases and code links (ADR-0011 §4).</summary>
internal static class PodiumDbModelIssues
{
    private const string NoBugFields = "bug_environment IS NULL AND repro_steps IS NULL AND expected_result IS NULL AND actual_result IS NULL AND severity IS NULL";

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Issue>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.Position, x.Id });
            e.HasIndex(x => new { x.ProjectId, x.Status });
            e.HasIndex(x => x.ApplicationId);
            e.HasIndex(x => x.AssigneeUserId);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.ComponentId);
            e.HasIndex(x => x.ReleaseId);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.AcceptanceCriteria).HasColumnType("jsonb");
            e.Property(x => x.Status).HasDefaultValue(IssueStatuses.Backlog);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_issues_type", PodiumDbModel.In("type", [.. WorkItemTypes.All]));
                t.HasCheckConstraint("ck_issues_status", PodiumDbModel.In("status", [.. IssueStatuses.All]));
                t.HasCheckConstraint("ck_issues_severity", $"severity IS NULL OR {PodiumDbModel.In("severity", [.. BugSeverities.All])}");
                t.HasCheckConstraint("ck_issues_bug_fields", $"type = 'bug' OR ({NoBugFields})");
                t.HasCheckConstraint("ck_issues_number", "number > 0");
                t.HasCheckConstraint("ck_issues_parent", "parent_id IS NULL OR parent_id <> id");
                t.HasCheckConstraint("ck_issues_criteria", "acceptance_criteria IS NULL OR (jsonb_typeof(acceptance_criteria) = 'array' AND jsonb_array_length(acceptance_criteria) <= 30)");
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Issue>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationComponent>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRelease>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AssigneeUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IssueLink>(e =>
        {
            e.HasIndex(x => new { x.BlockerIssueId, x.BlockedIssueId }).IsUnique();
            e.HasIndex(x => x.BlockedIssueId);
            e.HasIndex(x => x.ProjectId);
            e.ToTable(t => t.HasCheckConstraint("ck_issue_links_distinct", "blocker_issue_id <> blocked_issue_id"));
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManagedProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Issue>().WithMany().HasForeignKey(x => x.BlockerIssueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Issue>().WithMany().HasForeignKey(x => x.BlockedIssueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IssueTestCase>(e =>
        {
            e.HasIndex(x => new { x.IssueId, x.CreatedAt, x.Id });
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Result).HasDefaultValue(TestResults.NotRun);
            e.ToTable(t => t.HasCheckConstraint("ck_issue_test_cases_result", PodiumDbModel.In("result", [.. TestResults.All])));
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Issue>().WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ResultBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IssueCodeLink>(e =>
        {
            e.HasIndex(x => new { x.IssueId, x.Url }).IsUnique();
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Source).HasDefaultValue(CodeLinkSources.Manual);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_issue_code_links_kind", PodiumDbModel.In("kind", [.. CodeLinkKinds.All]));
                t.HasCheckConstraint("ck_issue_code_links_source", PodiumDbModel.In("source", [.. CodeLinkSources.All]));
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Issue>().WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IssueRun>(e =>
        {
            e.HasKey(x => new { x.IssueId, x.RunId });
            e.HasIndex(x => x.RunId);
            e.HasOne<Issue>().WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RemoteRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.LinkedBy).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
