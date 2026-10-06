using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoteRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing agent calls behind a brief ACCESS EXCLUSIVE lock.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.CreateTable(
                name: "remote_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_key = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    argv = table.Column<List<string>>(type: "text[]", nullable: true),
                    shell_command = table.Column<string>(type: "text", nullable: true),
                    cwd = table.Column<string>(type: "text", nullable: true),
                    resolved_exe = table.Column<string>(type: "text", nullable: true),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending_approval"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    output_bytes = table.Column<long>(type: "bigint", nullable: false),
                    output_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_runs", x => x.id);
                    table.CheckConstraint("ck_remote_runs_client_key", "char_length(client_key) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_remote_runs_mode", "mode IN ('argv', 'shell')");
                    table.CheckConstraint("ck_remote_runs_mode_shape", "(mode = 'argv' AND shell_command IS NULL) OR (mode = 'shell' AND argv IS NULL)");
                    table.CheckConstraint("ck_remote_runs_status", "status IN ('pending_approval', 'approved', 'delivered', 'running', 'succeeded', 'failed', 'timed_out', 'denied', 'expired', 'cancelled')");
                    table.CheckConstraint("ck_remote_runs_timeout", "timeout_seconds BETWEEN 1 AND 3600");
                    table.ForeignKey(
                        name: "fk_remote_runs_agents_requester_agent_id",
                        column: x => x.requester_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remote_runs_agents_target_agent_id",
                        column: x => x.target_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remote_runs_harness_sessions_requester_session_id",
                        column: x => x.requester_session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remote_runs_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remote_runs_users_requester_user_id",
                        column: x => x.requester_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remote_runs_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remote_runs_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_run_output",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    stream = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    bytes = table.Column<int>(type: "integer", nullable: false),
                    gap_before = table.Column<bool>(type: "boolean", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_run_output", x => new { x.run_id, x.seq });
                    table.CheckConstraint("ck_remote_run_output_bytes", "bytes BETWEEN 1 AND 65536");
                    table.CheckConstraint("ck_remote_run_output_seq", "seq >= 0");
                    table.CheckConstraint("ck_remote_run_output_stream", "stream IN ('stdout', 'stderr')");
                    table.ForeignKey(
                        name: "fk_remote_run_output_remote_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "remote_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_decided_by",
                table: "remote_runs",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_requester_agent_id_client_key",
                table: "remote_runs",
                columns: new[] { "requester_agent_id", "client_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_requester_session_id",
                table: "remote_runs",
                column: "requester_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_requester_user_id",
                table: "remote_runs",
                column: "requester_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_target_agent_id_created_at_id",
                table: "remote_runs",
                columns: new[] { "target_agent_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_target_agent_id_status",
                table: "remote_runs",
                columns: new[] { "target_agent_id", "status" },
                filter: "status IN ('approved', 'delivered', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_target_user_id_created_at",
                table: "remote_runs",
                columns: new[] { "target_user_id", "created_at" },
                filter: "status = 'pending_approval'");

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_workspace_id_created_at",
                table: "remote_runs",
                columns: new[] { "workspace_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remote_run_output");

            migrationBuilder.DropTable(
                name: "remote_runs");
        }
    }
}
