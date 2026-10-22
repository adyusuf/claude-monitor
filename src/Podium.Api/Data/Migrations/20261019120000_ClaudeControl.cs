using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions");

            migrationBuilder.AddColumn<bool>(
                name: "accepts_claude_control",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "accepts_claude_control_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "accepts_claude_control_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permission_mode",
                table: "agent_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "project_key",
                table: "agent_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "session_ref",
                table: "agent_actions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_accepts_claude_control_set_by",
                table: "agents",
                column: "accepts_claude_control_set_by");

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_accepts_claude_control_set_by",
                table: "agents",
                column: "accepts_claude_control_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions",
                sql: "kind IN ('restart', 'update_check', 'update', 'status', 'claude_start', 'claude_stop')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions");

            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_accepts_claude_control_set_by",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ix_agents_accepts_claude_control_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "accepts_claude_control",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "accepts_claude_control_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "accepts_claude_control_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "permission_mode",
                table: "agent_actions");

            migrationBuilder.DropColumn(
                name: "project_key",
                table: "agent_actions");

            migrationBuilder.DropColumn(
                name: "session_ref",
                table: "agent_actions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions",
                sql: "kind IN ('restart', 'update_check', 'update', 'status')");
        }
    }
}
