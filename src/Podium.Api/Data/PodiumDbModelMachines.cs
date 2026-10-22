using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Data;

/// <summary>Keys, CHECKs and column types of machine roles, presence and the tool inventory (ADR-0008 §4; docs/data-model.md §3). All nullable: NULL is "unset" or "unknown".</summary>
internal static class PodiumDbModelMachines
{
    private static string Optional(string column, IEnumerable<string> values) => $"{column} IS NULL OR " + PodiumDbModel.In(column, [.. values]);

    public static void Configure(ModelBuilder b)
    {
        b.Entity<Agent>(e =>
        {
            e.Property(x => x.Interpreters).HasColumnType("jsonb");
            e.Property(x => x.Tools).HasColumnType("jsonb");
            e.Property(x => x.Tags).HasColumnType("jsonb");
            e.Property(x => x.Services).HasColumnType("jsonb");
            e.Property(x => x.ClaudeInventory).HasColumnType("jsonb");
            e.Property(x => x.AdminNote).HasMaxLength(MachineTags.NoteMax);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TagsSetBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AdminNoteSetBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ShareServicesSetBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ShareInventorySetBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RoleSetBy).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_agents_role_environment", Optional("role_environment", MachineEnvironments.All));
                t.HasCheckConstraint("ck_agents_role_function", Optional("role_function", MachineFunctions.All));
                t.HasCheckConstraint("ck_agents_claimed_environment", Optional("claimed_environment", MachineEnvironments.All));
                t.HasCheckConstraint("ck_agents_claimed_function", Optional("claimed_function", MachineFunctions.All));
                t.HasCheckConstraint("ck_agents_claude_kind", Optional("claude_kind", ClaudeKinds.All));
                t.HasCheckConstraint("ck_agents_tags", $"tags IS NULL OR (jsonb_typeof(tags) = 'object' AND jsonb_array_length(jsonb_path_query_array(tags, '$.keyvalue()')) <= {MachineTags.Max})");
                t.HasCheckConstraint("ck_agents_services", "services IS NULL OR (jsonb_typeof(services) = 'array' AND jsonb_array_length(services) <= 64)");
                t.HasCheckConstraint("ck_agents_claude_sessions", "(claude_sessions_live IS NULL OR claude_sessions_live >= 0) AND (claude_sessions_idle IS NULL OR claude_sessions_idle >= 0)");
            });
        });

        b.Entity<MachineProject>(e =>
        {
            e.HasKey(x => new { x.AgentId, x.ProjectKey });
            e.HasIndex(x => new { x.WorkspaceId, x.ProjectKey });
            e.Property(x => x.ProjectKey).HasMaxLength(RunKinds.ProjectKeyMax);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.PathHmac).HasMaxLength(128);
            e.Property(x => x.Branch).HasMaxLength(200);
            e.Property(x => x.HeadSha).HasMaxLength(64);
            e.Property(x => x.Tag).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(16);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_machine_projects_source", PodiumDbModel.In("source", [.. ProjectSources.All])));
        });

        b.Entity<WorkspaceSettings>(e =>
        {
            e.Property(x => x.ScriptOutputMaxBytes).HasDefaultValue(8L * 1024 * 1024);
            e.ToTable(t => t.HasCheckConstraint("ck_workspace_settings_script_output", "script_output_max_bytes BETWEEN 1048576 AND 67108864"));
        });

        b.Entity<RemoteRun>(e =>
        {
            e.Property(x => x.ProjectKey).HasMaxLength(RunKinds.ProjectKeyMax);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.ScriptInterpreter).HasMaxLength(16);
            e.Property(x => x.ScriptFilesJson).HasColumnType("jsonb");
            e.Property(x => x.TargetEnvironment).HasMaxLength(16);
            e.Property(x => x.TargetFunction).HasMaxLength(16);
            e.HasIndex(x => new { x.TargetAgentId, x.ProjectKey, x.CreatedAt }).IsDescending(false, false, true).HasFilter("project_key IS NOT NULL");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_remote_runs_kind", Optional("kind", RunKinds.All));
                t.HasCheckConstraint("ck_remote_runs_script_interpreter", Optional("script_interpreter", ScriptInterpreters.All));
                t.HasCheckConstraint("ck_remote_runs_target_role", "(" + Optional("target_environment", MachineEnvironments.All) + ") AND (" + Optional("target_function", MachineFunctions.All) + ")");
                t.HasCheckConstraint("ck_remote_runs_script_extras", "(script_files_json IS NULL AND script_stdin IS NULL) OR mode = 'script'");
                t.HasCheckConstraint("ck_remote_runs_output_cap", "output_cap IS NULL OR output_cap BETWEEN 1048576 AND 67108864");
                t.HasCheckConstraint("ck_remote_runs_tests", "(tests_passed IS NULL OR tests_passed >= 0) AND (tests_failed IS NULL OR tests_failed >= 0) AND (tests_skipped IS NULL OR tests_skipped >= 0)");
            });
        });
    }
}
