using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinuxMachines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing every agent call behind the brief ACCESS EXCLUSIVE lock on machines.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.DropCheckConstraint(
                name: "ck_machines_os",
                table: "machines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_machines_os",
                table: "machines",
                sql: "os IN ('macos', 'windows', 'linux')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_machines_os",
                table: "machines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_machines_os",
                table: "machines",
                sql: "os IN ('macos', 'windows')");
        }
    }
}
