using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing agent calls behind a brief ACCESS EXCLUSIVE lock.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.CreateTable(
                name: "machine_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_search = table.Column<string>(type: "text", nullable: false),
                    argv = table.Column<List<string>>(type: "text[]", nullable: true),
                    cwd = table.Column<string>(type: "text", nullable: true),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    proposed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_jobs", x => x.id);
                    table.CheckConstraint("ck_machine_jobs_status", "status IN ('proposed', 'active', 'denied', 'retired')");
                    table.CheckConstraint("ck_machine_jobs_timeout", "timeout_seconds BETWEEN 1 AND 3600");
                    table.ForeignKey(
                        name: "fk_machine_jobs_agents_proposed_by_agent_id",
                        column: x => x.proposed_by_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_jobs_agents_target_agent_id",
                        column: x => x.target_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_jobs_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_jobs_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_jobs_users_proposed_by_user_id",
                        column: x => x.proposed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_jobs_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_job_id",
                table: "remote_runs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_jobs_decided_by",
                table: "machine_jobs",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_machine_jobs_owner_user_id",
                table: "machine_jobs",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_jobs_proposed_by_agent_id",
                table: "machine_jobs",
                column: "proposed_by_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_jobs_proposed_by_user_id",
                table: "machine_jobs",
                column: "proposed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_jobs_target_agent_id_name_search",
                table: "machine_jobs",
                columns: new[] { "target_agent_id", "name_search" },
                unique: true,
                filter: "status IN ('proposed', 'active')");

            migrationBuilder.CreateIndex(
                name: "ix_machine_jobs_workspace_id",
                table: "machine_jobs",
                column: "workspace_id");

            migrationBuilder.AddForeignKey(
                name: "fk_remote_runs_machine_jobs_job_id",
                table: "remote_runs",
                column: "job_id",
                principalTable: "machine_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_remote_runs_machine_jobs_job_id",
                table: "remote_runs");

            migrationBuilder.DropTable(
                name: "machine_jobs");

            migrationBuilder.DropIndex(
                name: "ix_remote_runs_job_id",
                table: "remote_runs");
        }
    }
}
