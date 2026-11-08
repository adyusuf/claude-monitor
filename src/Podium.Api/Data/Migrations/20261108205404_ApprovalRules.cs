using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "answered_by_rule",
                table: "permission_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "approval_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    command = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    duration = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_from_request = table.Column<Guid>(type: "uuid", nullable: false),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_rules", x => x.id);
                    table.CheckConstraint("ck_approval_rules_duration", "duration IN ('5m', '10m', '30m', '1h', 'always')");
                    table.CheckConstraint("ck_approval_rules_expiry", "(duration = 'always') = (expires_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_approval_rules_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approval_rules_agent_id_tool_name",
                table: "approval_rules",
                columns: new[] { "agent_id", "tool_name" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_rules_user_id_workspace_id",
                table: "approval_rules",
                columns: new[] { "user_id", "workspace_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_rules");

            migrationBuilder.DropColumn(
                name: "answered_by_rule",
                table: "permission_requests");
        }
    }
}
