using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing agent calls behind a brief ACCESS EXCLUSIVE lock.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.CreateTable(
                name: "machine_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grantee_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template = table.Column<List<string>>(type: "text[]", nullable: true),
                    cwd = table.Column<string>(type: "text", nullable: true),
                    max_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    template_hash = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_grants", x => x.id);
                    table.CheckConstraint("ck_machine_grants_expiry", "expires_at <= created_at + interval '90 days'");
                    table.CheckConstraint("ck_machine_grants_status", "status IN ('requested', 'active', 'denied', 'revoked', 'expired')");
                    table.CheckConstraint("ck_machine_grants_timeout", "max_timeout_seconds BETWEEN 1 AND 3600");
                    table.ForeignKey(
                        name: "fk_machine_grants_agents_grantee_agent_id",
                        column: x => x.grantee_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_agents_requested_by_agent_id",
                        column: x => x.requested_by_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_agents_target_agent_id",
                        column: x => x.target_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_users_grantee_user_id",
                        column: x => x.grantee_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_users_revoked_by",
                        column: x => x.revoked_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_grants_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remote_runs_grant_id",
                table: "remote_runs",
                column: "grant_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_decided_by",
                table: "machine_grants",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_grantee_agent_id",
                table: "machine_grants",
                column: "grantee_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_grantee_user_id",
                table: "machine_grants",
                column: "grantee_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_owner_user_id",
                table: "machine_grants",
                column: "owner_user_id",
                filter: "status = 'requested'");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_requested_by_agent_id",
                table: "machine_grants",
                column: "requested_by_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_revoked_by",
                table: "machine_grants",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_target_agent_id_grantee_user_id",
                table: "machine_grants",
                columns: new[] { "target_agent_id", "grantee_user_id" },
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_target_agent_id_grantee_user_id_template_hash",
                table: "machine_grants",
                columns: new[] { "target_agent_id", "grantee_user_id", "template_hash" },
                unique: true,
                filter: "status IN ('requested', 'active')");

            migrationBuilder.CreateIndex(
                name: "ix_machine_grants_workspace_id",
                table: "machine_grants",
                column: "workspace_id");

            migrationBuilder.AddForeignKey(
                name: "fk_remote_runs_machine_grants_grant_id",
                table: "remote_runs",
                column: "grant_id",
                principalTable: "machine_grants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_remote_runs_machine_grants_grant_id",
                table: "remote_runs");

            migrationBuilder.DropTable(
                name: "machine_grants");

            migrationBuilder.DropIndex(
                name: "ix_remote_runs_grant_id",
                table: "remote_runs");
        }
    }
}
