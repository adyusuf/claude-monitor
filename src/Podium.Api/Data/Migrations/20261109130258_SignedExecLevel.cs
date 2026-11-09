using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignedExecLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "exec_level_report",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "exec_level_report_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "exec_level_request",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "exec_level_sequence",
                table: "agents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "exec_level_report",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "exec_level_report_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "exec_level_request",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "exec_level_sequence",
                table: "agents");
        }
    }
}
