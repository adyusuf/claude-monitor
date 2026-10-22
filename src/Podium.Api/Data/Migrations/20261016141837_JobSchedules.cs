using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "remote_schedules_enabled",
                table: "workspace_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "schedule_id",
                table: "remote_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "job_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    interval_minutes = table.Column<int>(type: "integer", nullable: true),
                    daily_at_minutes = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "active"),
                    pause_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_run_settled = table.Column<bool>(type: "boolean", nullable: false),
                    failures_in_arow = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_schedules", x => x.id);
                    table.CheckConstraint("ck_job_schedules_failures", "failures_in_arow >= 0");
                    table.CheckConstraint("ck_job_schedules_kind", "kind IN ('interval', 'daily')");
                    table.CheckConstraint("ck_job_schedules_shape", "(kind = 'interval' AND interval_minutes BETWEEN 15 AND 10080 AND daily_at_minutes IS NULL) OR (kind = 'daily' AND daily_at_minutes BETWEEN 0 AND 1439 AND interval_minutes IS NULL)");
                    table.CheckConstraint("ck_job_schedules_status", "status IN ('active', 'paused', 'ended')");
                    table.ForeignKey(
                        name: "fk_job_schedules_agents_target_agent_id",
                        column: x => x.target_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_job_schedules_machine_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "machine_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_job_schedules_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_job_schedules_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_schedule_id",
                table: "remote_runs",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_schedules_job_id",
                table: "job_schedules",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_schedules_next_run_at",
                table: "job_schedules",
                column: "next_run_at",
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_job_schedules_owner_user_id",
                table: "job_schedules",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_schedules_target_agent_id",
                table: "job_schedules",
                column: "target_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_schedules_workspace_id",
                table: "job_schedules",
                column: "workspace_id");

            migrationBuilder.AddForeignKey(
                name: "fk_remote_runs_job_schedules_schedule_id",
                table: "remote_runs",
                column: "schedule_id",
                principalTable: "job_schedules",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_remote_runs_job_schedules_schedule_id",
                table: "remote_runs");

            migrationBuilder.DropTable(
                name: "job_schedules");

            migrationBuilder.DropIndex(
                name: "ix_remote_runs_schedule_id",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "remote_schedules_enabled",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "schedule_id",
                table: "remote_runs");
        }
    }
}
