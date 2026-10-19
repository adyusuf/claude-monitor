using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectTestCommand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "test_argv",
                table: "machine_projects",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "test_cwd",
                table: "machine_projects",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "test_argv",
                table: "machine_projects");

            migrationBuilder.DropColumn(
                name: "test_cwd",
                table: "machine_projects");
        }
    }
}
