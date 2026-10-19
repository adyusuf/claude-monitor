using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <summary>
    /// ADR-0011: the table of work folders sessions report ("projects") becomes "repos", so "project" can name the managed
    /// business entity. A rename, not a drop and create: the rows and their ids stay. The one deliberate exception to the
    /// additive-only rule (global #4), approved by the maintainer on 19/10/2026.
    /// </summary>
    public partial class RenameProjectsToRepos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "projects", newName: "repos");
            migrationBuilder.RenameColumn(name: "project_id", table: "harness_sessions", newName: "repo_id");
            migrationBuilder.RenameIndex(name: "ix_projects_workspace_id_key", table: "repos", newName: "ix_repos_workspace_id_key");
            migrationBuilder.RenameIndex(name: "ix_harness_sessions_project_id_last_event_at", table: "harness_sessions",
                newName: "ix_harness_sessions_repo_id_last_event_at");
            migrationBuilder.Sql("ALTER TABLE repos RENAME CONSTRAINT pk_projects TO pk_repos;");
            migrationBuilder.Sql("ALTER TABLE repos RENAME CONSTRAINT fk_projects_workspaces_workspace_id TO fk_repos_workspaces_workspace_id;");
            migrationBuilder.Sql("ALTER TABLE harness_sessions RENAME CONSTRAINT fk_harness_sessions_projects_project_id TO fk_harness_sessions_repos_repo_id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE harness_sessions RENAME CONSTRAINT fk_harness_sessions_repos_repo_id TO fk_harness_sessions_projects_project_id;");
            migrationBuilder.Sql("ALTER TABLE repos RENAME CONSTRAINT fk_repos_workspaces_workspace_id TO fk_projects_workspaces_workspace_id;");
            migrationBuilder.Sql("ALTER TABLE repos RENAME CONSTRAINT pk_repos TO pk_projects;");
            migrationBuilder.RenameIndex(name: "ix_harness_sessions_repo_id_last_event_at", table: "harness_sessions",
                newName: "ix_harness_sessions_project_id_last_event_at");
            migrationBuilder.RenameIndex(name: "ix_repos_workspace_id_key", table: "repos", newName: "ix_projects_workspace_id_key");
            migrationBuilder.RenameColumn(name: "repo_id", table: "harness_sessions", newName: "project_id");
            migrationBuilder.RenameTable(name: "repos", newName: "projects");
        }
    }
}
