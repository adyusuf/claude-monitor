using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Issues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "issues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    title_search = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: true),
                    estimate = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "backlog"),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    component_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    acceptance_criteria = table.Column<string>(type: "jsonb", nullable: true),
                    technical_design = table.Column<string>(type: "text", nullable: true),
                    bug_environment = table.Column<string>(type: "text", nullable: true),
                    repro_steps = table.Column<string>(type: "text", nullable: true),
                    expected_result = table.Column<string>(type: "text", nullable: true),
                    actual_result = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<double>(type: "double precision", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issues", x => x.id);
                    table.CheckConstraint("ck_issues_bug_fields", "type = 'bug' OR (bug_environment IS NULL AND repro_steps IS NULL AND expected_result IS NULL AND actual_result IS NULL AND severity IS NULL)");
                    table.CheckConstraint("ck_issues_criteria", "acceptance_criteria IS NULL OR (jsonb_typeof(acceptance_criteria) = 'array' AND jsonb_array_length(acceptance_criteria) <= 30)");
                    table.CheckConstraint("ck_issues_number", "number > 0");
                    table.CheckConstraint("ck_issues_parent", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_issues_severity", "severity IS NULL OR severity IN ('critical', 'major', 'minor', 'trivial')");
                    table.CheckConstraint("ck_issues_status", "status IN ('backlog', 'ready', 'in_progress', 'in_review', 'done', 'cancelled')");
                    table.CheckConstraint("ck_issues_type", "type IN ('epic', 'story', 'task', 'bug', 'spike')");
                    table.ForeignKey(
                        name: "fk_issues_application_components_component_id",
                        column: x => x.component_id,
                        principalTable: "application_components",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_application_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "application_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_issues_parent_id",
                        column: x => x.parent_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_users_assignee_user_id",
                        column: x => x.assignee_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "issue_code_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false, defaultValue: "manual"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_code_links", x => x.id);
                    table.CheckConstraint("ck_issue_code_links_kind", "kind IN ('pr', 'commit')");
                    table.CheckConstraint("ck_issue_code_links_source", "source IN ('manual', 'auto')");
                    table.ForeignKey(
                        name: "fk_issue_code_links_issues_issue_id",
                        column: x => x.issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_code_links_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_code_links_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "issue_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocker_issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_links", x => x.id);
                    table.CheckConstraint("ck_issue_links_distinct", "blocker_issue_id <> blocked_issue_id");
                    table.ForeignKey(
                        name: "fk_issue_links_issues_blocked_issue_id",
                        column: x => x.blocked_issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_links_issues_blocker_issue_id",
                        column: x => x.blocker_issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_links_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_links_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "issue_test_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    steps = table.Column<string>(type: "text", nullable: true),
                    expected = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "text", nullable: false, defaultValue: "not_run"),
                    result_note = table.Column<string>(type: "text", nullable: true),
                    result_by = table.Column<Guid>(type: "uuid", nullable: true),
                    result_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_test_cases", x => x.id);
                    table.CheckConstraint("ck_issue_test_cases_result", "result IN ('not_run', 'passed', 'failed', 'blocked')");
                    table.ForeignKey(
                        name: "fk_issue_test_cases_issues_issue_id",
                        column: x => x.issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_test_cases_users_result_by",
                        column: x => x.result_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issue_test_cases_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_issue_code_links_created_by",
                table: "issue_code_links",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_issue_code_links_issue_id_url",
                table: "issue_code_links",
                columns: new[] { "issue_id", "url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issue_code_links_workspace_id",
                table: "issue_code_links",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_links_blocked_issue_id",
                table: "issue_links",
                column: "blocked_issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_links_blocker_issue_id_blocked_issue_id",
                table: "issue_links",
                columns: new[] { "blocker_issue_id", "blocked_issue_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issue_links_created_by",
                table: "issue_links",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_issue_links_project_id",
                table: "issue_links",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_links_workspace_id",
                table: "issue_links",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_test_cases_issue_id_created_at_id",
                table: "issue_test_cases",
                columns: new[] { "issue_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_test_cases_result_by",
                table: "issue_test_cases",
                column: "result_by");

            migrationBuilder.CreateIndex(
                name: "ix_issue_test_cases_workspace_id",
                table: "issue_test_cases",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_application_id",
                table: "issues",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_assignee_user_id",
                table: "issues",
                column: "assignee_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_component_id",
                table: "issues",
                column: "component_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_created_by",
                table: "issues",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_issues_parent_id",
                table: "issues",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_project_id_number",
                table: "issues",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issues_project_id_position_id",
                table: "issues",
                columns: new[] { "project_id", "position", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_project_id_status",
                table: "issues",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_release_id",
                table: "issues",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_updated_by",
                table: "issues",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id",
                table: "issues",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issue_code_links");

            migrationBuilder.DropTable(
                name: "issue_links");

            migrationBuilder.DropTable(
                name: "issue_test_cases");

            migrationBuilder.DropTable(
                name: "issues");
        }
    }
}
