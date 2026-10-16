using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScriptRunFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "script_files_json",
                table: "remote_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "script_stdin",
                table: "remote_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_script_extras",
                table: "remote_runs",
                sql: "(script_files_json IS NULL AND script_stdin IS NULL) OR mode = 'script'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_script_extras",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "script_files_json",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "script_stdin",
                table: "remote_runs");
        }
    }
}
