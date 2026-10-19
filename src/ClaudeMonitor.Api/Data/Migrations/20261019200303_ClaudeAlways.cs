using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeAlways : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claude_auto_mode",
                table: "agents");

            migrationBuilder.AddColumn<string>(
                name: "claude_auto_project",
                table: "agents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claude_auto_mode",
                table: "agents",
                sql: "claude_auto_mode IS NULL OR claude_auto_mode IN ('off', 'on_message', 'always')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claude_auto_mode",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_auto_project",
                table: "agents");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claude_auto_mode",
                table: "agents",
                sql: "claude_auto_mode IS NULL OR claude_auto_mode IN ('off', 'on_message')");
        }
    }
}
