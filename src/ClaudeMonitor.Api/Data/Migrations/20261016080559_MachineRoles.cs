using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "capabilities",
                table: "agents",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claimed_environment",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claimed_function",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claude_kind",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claude_path",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "claude_sessions_idle",
                table: "agents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "claude_sessions_live",
                table: "agents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claude_version",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "interpreters",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "presence_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role_environment",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role_function",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "role_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "role_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tools",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_role_set_by",
                table: "agents",
                column: "role_set_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claimed_environment",
                table: "agents",
                sql: "claimed_environment IS NULL OR claimed_environment IN ('dev', 'test', 'stage', 'preprod', 'production', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claimed_function",
                table: "agents",
                sql: "claimed_function IS NULL OR claimed_function IN ('development', 'application', 'web', 'database', 'ci', 'storage', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claude_kind",
                table: "agents",
                sql: "claude_kind IS NULL OR claude_kind IN ('none', 'npm', 'native', 'unsupported')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claude_sessions",
                table: "agents",
                sql: "(claude_sessions_live IS NULL OR claude_sessions_live >= 0) AND (claude_sessions_idle IS NULL OR claude_sessions_idle >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_role_environment",
                table: "agents",
                sql: "role_environment IS NULL OR role_environment IN ('dev', 'test', 'stage', 'preprod', 'production', 'other')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_role_function",
                table: "agents",
                sql: "role_function IS NULL OR role_function IN ('development', 'application', 'web', 'database', 'ci', 'storage', 'other')");

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_role_set_by",
                table: "agents",
                column: "role_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_role_set_by",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ix_agents_role_set_by",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claimed_environment",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claimed_function",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claude_kind",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claude_sessions",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_role_environment",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_role_function",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "capabilities",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claimed_environment",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claimed_function",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_kind",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_path",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_sessions_idle",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_sessions_live",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_version",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "interpreters",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "presence_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role_environment",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role_function",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "tools",
                table: "agents");
        }
    }
}
