using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdateSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "agent_update",
                table: "workspace_settings",
                type: "text",
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspace_settings_agent_update",
                table: "workspace_settings",
                sql: "agent_update IN ('off', 'check', 'on')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workspace_settings_agent_update",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "agent_update",
                table: "workspace_settings");
        }
    }
}
