using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Sprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "sprint_id",
                table: "issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sprints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    goal = table.Column<string>(type: "text", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    capacity = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "planned"),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprints", x => x.id);
                    table.CheckConstraint("ck_sprints_capacity", "capacity IS NULL OR capacity >= 0");
                    table.CheckConstraint("ck_sprints_dates", "start_date IS NULL OR end_date IS NULL OR end_date >= start_date");
                    table.CheckConstraint("ck_sprints_number", "number > 0");
                    table.CheckConstraint("ck_sprints_status", "status IN ('planned', 'active', 'closed')");
                    table.ForeignKey(
                        name: "fk_sprints_application_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "application_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprints_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprints_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprints_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprints_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprints_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sprint_members",
                columns: table => new
                {
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprint_members", x => new { x.sprint_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_sprint_members_sprints_sprint_id",
                        column: x => x.sprint_id,
                        principalTable: "sprints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprint_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_issues_sprint_id",
                table: "issues",
                column: "sprint_id");

            migrationBuilder.CreateIndex(
                name: "ix_sprint_members_user_id",
                table: "sprint_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sprints_created_by",
                table: "sprints",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_sprints_project_id_number",
                table: "sprints",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sprints_release_id",
                table: "sprints",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "ix_sprints_updated_by",
                table: "sprints",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_sprints_workspace_id",
                table: "sprints",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_application",
                table: "sprints",
                column: "application_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_project",
                table: "sprints",
                column: "project_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_issues_sprints_sprint_id",
                table: "issues",
                column: "sprint_id",
                principalTable: "sprints",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_issues_sprints_sprint_id",
                table: "issues");

            migrationBuilder.DropTable(
                name: "sprint_members");

            migrationBuilder.DropTable(
                name: "sprints");

            migrationBuilder.DropIndex(
                name: "ix_issues_sprint_id",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "sprint_id",
                table: "issues");
        }
    }
}
