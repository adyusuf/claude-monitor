using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScriptRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_mode",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_mode_shape",
                table: "remote_runs");

            migrationBuilder.AddColumn<long>(
                name: "script_output_max_bytes",
                table: "workspace_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 8388608L);

            migrationBuilder.AddColumn<bool>(
                name: "approved_with_totp",
                table: "remote_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "output_cap",
                table: "remote_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "output_chunks",
                table: "remote_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "output_complete",
                table: "remote_runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_dropped_bytes",
                table: "remote_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "output_missing_from_seq",
                table: "remote_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_read_bytes",
                table: "remote_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "output_refused_bytes",
                table: "remote_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "script_args",
                table: "remote_runs",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "script_body",
                table: "remote_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "script_interpreter",
                table: "remote_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_environment",
                table: "remote_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_function",
                table: "remote_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspace_settings_script_output",
                table: "workspace_settings",
                sql: "script_output_max_bytes BETWEEN 1048576 AND 67108864");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_mode",
                table: "remote_runs",
                sql: "mode IN ('argv', 'shell', 'script')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_mode_shape",
                table: "remote_runs",
                sql: "(mode = 'argv' AND shell_command IS NULL AND script_body IS NULL) OR (mode = 'shell' AND argv IS NULL AND script_body IS NULL) OR (mode = 'script' AND argv IS NULL AND shell_command IS NULL AND cwd IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_output_cap",
                table: "remote_runs",
                sql: "output_cap IS NULL OR output_cap BETWEEN 1048576 AND 67108864");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_script_interpreter",
                table: "remote_runs",
                sql: "script_interpreter IS NULL OR script_interpreter IN ('sh', 'bash', 'powershell', 'python')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_target_role",
                table: "remote_runs",
                sql: "(target_environment IS NULL OR target_environment IN ('dev', 'test', 'stage', 'preprod', 'production', 'other')) AND (target_function IS NULL OR target_function IN ('development', 'application', 'web', 'database', 'ci', 'storage', 'other'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workspace_settings_script_output",
                table: "workspace_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_mode",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_mode_shape",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_output_cap",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_script_interpreter",
                table: "remote_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_remote_runs_target_role",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "script_output_max_bytes",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "approved_with_totp",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_cap",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_chunks",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_complete",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_dropped_bytes",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_missing_from_seq",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_read_bytes",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "output_refused_bytes",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "script_args",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "script_body",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "script_interpreter",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "target_environment",
                table: "remote_runs");

            migrationBuilder.DropColumn(
                name: "target_function",
                table: "remote_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_mode",
                table: "remote_runs",
                sql: "mode IN ('argv', 'shell')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_remote_runs_mode_shape",
                table: "remote_runs",
                sql: "(mode = 'argv' AND shell_command IS NULL) OR (mode = 'shell' AND argv IS NULL)");
        }
    }
}
