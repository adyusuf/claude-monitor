using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeAutoMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "claude_auto_mode",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_claude_auto_mode",
                table: "agents",
                sql: "claude_auto_mode IS NULL OR claude_auto_mode IN ('off', 'on_message')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_claude_auto_mode",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "claude_auto_mode",
                table: "agents");
        }
    }
}
