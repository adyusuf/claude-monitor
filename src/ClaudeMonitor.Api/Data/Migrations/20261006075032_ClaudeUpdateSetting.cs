using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeUpdateSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "claude_update",
                table: "workspace_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "claude_update",
                table: "workspace_settings");
        }
    }
}
