using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing agent calls behind a brief ACCESS EXCLUSIVE lock.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<int>(
                name: "alert_cpu_pct",
                table: "workspace_settings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "alert_disk_pct",
                table: "workspace_settings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "alert_memory_pct",
                table: "workspace_settings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "alert_sustain_seconds",
                table: "workspace_settings",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<bool>(
                name: "remote_runs_enabled",
                table: "workspace_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "exec_level",
                table: "agents",
                type: "text",
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "profile_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "service_mode",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "machine_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    state = table.Column<string>(type: "text", nullable: false),
                    threshold_pct = table.Column<float>(type: "real", nullable: false),
                    last_value = table.Column<float>(type: "real", nullable: false),
                    peak_value = table.Column<float>(type: "real", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_alerts", x => x.id);
                    table.CheckConstraint("ck_machine_alerts_kind", "kind IN ('cpu', 'memory', 'disk', 'offline')");
                    table.CheckConstraint("ck_machine_alerts_state", "state IN ('open', 'resolved')");
                    table.ForeignKey(
                        name: "fk_machine_alerts_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_alerts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "machine_metrics",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sampled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cpu_pct = table.Column<float>(type: "real", nullable: false),
                    mem_used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mem_total_bytes = table.Column<long>(type: "bigint", nullable: false),
                    disks = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_metrics", x => new { x.agent_id, x.sampled_at });
                    table.CheckConstraint("ck_machine_metrics_cpu", "cpu_pct BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_machine_metrics_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_metrics_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspace_settings_alerts",
                table: "workspace_settings",
                sql: "alert_cpu_pct BETWEEN 1 AND 100 AND alert_memory_pct BETWEEN 1 AND 100 AND alert_disk_pct BETWEEN 1 AND 100 AND alert_sustain_seconds BETWEEN 60 AND 86400");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_exec_level",
                table: "agents",
                sql: "exec_level IN ('off', 'argv', 'shell')");

            migrationBuilder.CreateIndex(
                name: "ix_machine_alerts_agent_id_kind_subject",
                table: "machine_alerts",
                columns: new[] { "agent_id", "kind", "subject" },
                unique: true,
                filter: "state = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_machine_alerts_workspace_id_opened_at_id",
                table: "machine_alerts",
                columns: new[] { "workspace_id", "opened_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_machine_metrics_sampled_at",
                table: "machine_metrics",
                column: "sampled_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_machine_metrics_workspace_id",
                table: "machine_metrics",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "machine_alerts");

            migrationBuilder.DropTable(
                name: "machine_metrics");

            migrationBuilder.DropCheckConstraint(
                name: "ck_workspace_settings_alerts",
                table: "workspace_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_exec_level",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "alert_cpu_pct",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "alert_disk_pct",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "alert_memory_pct",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "alert_sustain_seconds",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "remote_runs_enabled",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "exec_level",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "profile_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "service_mode",
                table: "agents");
        }
    }
}
