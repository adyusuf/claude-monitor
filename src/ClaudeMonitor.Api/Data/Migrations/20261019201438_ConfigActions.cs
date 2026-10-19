using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions");

            migrationBuilder.AddColumn<string>(
                name: "target",
                table: "agent_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "value",
                table: "agent_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions",
                sql: "kind IN ('restart', 'update_check', 'update', 'status', 'claude_start', 'claude_stop', 'plugin_enable', 'plugin_disable', 'plugin_update', 'plugin_install', 'project_setting')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions");

            migrationBuilder.DropColumn(
                name: "target",
                table: "agent_actions");

            migrationBuilder.DropColumn(
                name: "value",
                table: "agent_actions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_actions_kind",
                table: "agent_actions",
                sql: "kind IN ('restart', 'update_check', 'update', 'status', 'claude_start', 'claude_stop')");
        }
    }
}
