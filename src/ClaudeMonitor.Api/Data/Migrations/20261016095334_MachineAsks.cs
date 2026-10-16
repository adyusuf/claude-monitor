using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineAsks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "accepts_asks",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "accepts_asks_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "accepts_asks_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "machine_asks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_session = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    client_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    project_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    question = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "queued"),
                    answer = table.Column<string>(type: "text", nullable: true),
                    answer_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_asks", x => x.id);
                    table.CheckConstraint("ck_machine_asks_answer", "answer IS NULL OR char_length(answer) <= 100000");
                    table.CheckConstraint("ck_machine_asks_question", "question IS NULL OR char_length(question) BETWEEN 1 AND 8000");
                    table.CheckConstraint("ck_machine_asks_same_user", "requester_user_id = target_user_id");
                    table.CheckConstraint("ck_machine_asks_status", "status IN ('queued', 'delivered', 'answered', 'no_idle_session', 'not_accepting', 'no_answer', 'expired', 'cancelled')");
                    table.CheckConstraint("ck_machine_asks_two_agents", "requester_agent_id <> target_agent_id");
                    table.ForeignKey(
                        name: "fk_machine_asks_agents_requester_agent_id",
                        column: x => x.requester_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_asks_agents_target_agent_id",
                        column: x => x.target_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_asks_harness_sessions_requester_session_id",
                        column: x => x.requester_session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_asks_users_requester_user_id",
                        column: x => x.requester_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_asks_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_machine_asks_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agents_accepts_asks_set_by",
                table: "agents",
                column: "accepts_asks_set_by");

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_requester_agent_id_client_key",
                table: "machine_asks",
                columns: new[] { "requester_agent_id", "client_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_requester_agent_id_created_at",
                table: "machine_asks",
                columns: new[] { "requester_agent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_requester_session_id",
                table: "machine_asks",
                column: "requester_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_requester_user_id",
                table: "machine_asks",
                column: "requester_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_target_agent_id_status",
                table: "machine_asks",
                columns: new[] { "target_agent_id", "status" },
                filter: "status IN ('queued', 'delivered')");

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_target_user_id",
                table: "machine_asks",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_asks_workspace_id_created_at",
                table: "machine_asks",
                columns: new[] { "workspace_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_accepts_asks_set_by",
                table: "agents",
                column: "accepts_asks_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_accepts_asks_set_by",
                table: "agents");

            migrationBuilder.DropTable(
                name: "machine_asks");

            migrationBuilder.DropIndex(
                name: "ix_agents_accepts_asks_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "accepts_asks",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "accepts_asks_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "accepts_asks_set_by",
                table: "agents");
        }
    }
}
