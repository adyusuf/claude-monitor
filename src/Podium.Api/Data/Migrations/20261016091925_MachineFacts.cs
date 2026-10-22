using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "remote_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "project_key",
                table: "remote_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tests_failed",
                table: "remote_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tests_passed",
                table: "remote_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tests_skipped",
                table: "remote_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "admin_note",
                table: "agents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "admin_note_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "admin_note_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "services",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "services_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "share_services",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "share_services_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "share_services_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "tags_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tags_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "machine_projects",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    path_hmac = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_projects", x => new { x.agent_id, x.project_key });
                    table.CheckConstraint("ck_machine_projects_source", "source IN ('session', 'listed')");
                    table.ForeignKey(
                        name: "fk_machine_projects_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_projects_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_target_agent_id_project_key_created_at",
                table: "remote_runs",
                columns: new[] { "target_agent_id", "project_key", "created_at" },
                descending: new[] { false, false, true },
                filter: "project_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_kind",
                table: "remote_runs",
                sql: "kind IS NULL OR kind IN ('test', 'build', 'deploy', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_tests",
                table: "remote_runs",
                sql: "(tests_passed IS NULL OR tests_passed >= 0) AND (tests_failed IS NULL OR tests_failed >= 0) AND (tests_skipped IS NULL OR tests_skipped >= 0)");

            migrationBuilder.CreateIndex(
                name: "ix_agents_admin_note_set_by",
                table: "agents",
                column: "admin_note_set_by");

            migrationBuilder.CreateIndex(
                name: "ix_agents_share_services_set_by",
                table: "agents",
                column: "share_services_set_by");

            migrationBuilder.CreateIndex(
                name: "ix_agents_tags_set_by",
                table: "agents",
                column: "tags_set_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_services",
                table: "agents",
                sql: "services IS NULL OR (jsonb_typeof(services) = 'array' AND jsonb_array_length(services) <= 64)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_tags",
                table: "agents",
                sql: "tags IS NULL OR (jsonb_typeof(tags) = 'object' AND jsonb_array_length(jsonb_path_query_array(tags, '$.keyvalue()')) <= 10)");

            migrationBuilder.CreateIndex(
                name: "ix_machine_projects_workspace_id_project_key",
                table: "machine_projects",
                columns: new[] { "workspace_id", "project_key" });

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_admin_note_set_by",
                table: "agents",
                column: "admin_note_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_share_services_set_by",
                table: "agents",
                column: "share_services_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_tags_set_by",
                table: "agents",
                column: "tags_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_admin_note_set_by",
                table: "agents");

            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_share_services_set_by",
                table: "agents");

            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_tags_set_by",
                table: "agents");

            migrationBuilder.DropTable(
                name: "machine_projects");

            migrationBuilder.DropIndex(
                name: "ix_remote_runs_target_agent_id_project_key_created_at",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_kind",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_tests",
                table: "remote_runs");

            migrationBuilder.DropIndex(
                name: "ix_agents_admin_note_set_by",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ix_agents_share_services_set_by",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ix_agents_tags_set_by",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_services",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_tags",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "project_key",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "tests_failed",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "tests_passed",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "tests_skipped",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "admin_note",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "admin_note_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "admin_note_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "services",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "services_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "share_services",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "share_services_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "share_services_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "tags_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "tags_set_by",
                table: "agents");
        }
    }
}
