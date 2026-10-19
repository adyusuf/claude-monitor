using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeNewSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "claude_trusted",
                table: "machine_projects",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_prompt",
                table: "agent_actions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "claude_trusted",
                table: "machine_projects");

            migrationBuilder.DropColumn(
                name: "first_prompt",
                table: "agent_actions");
        }
    }
}
