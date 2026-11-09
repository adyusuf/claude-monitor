using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectAccessPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enforce_access_policies",
                table: "workspace_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "access_scope",
                table: "permission_requests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "access_report",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "access_report_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_access_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    document_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cleared = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    signed_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    signature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    signed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_access_policies", x => x.id);
                    table.CheckConstraint("ck_project_access_policies_signed", "(signed_key_id IS NULL) = (signature IS NULL)");
                    table.CheckConstraint("ck_project_access_policies_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_project_access_policies_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_access_policies_users_signed_by",
                        column: x => x.signed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_project_access_policies_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_project_access_policies_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_access_policy_keys",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_access_policy_keys", x => new { x.policy_id, x.project_key });
                    table.ForeignKey(
                        name: "fk_project_access_policy_keys_project_access_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "project_access_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_access_policy_keys_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_permission_requests_access_scope",
                table: "permission_requests",
                sql: "access_scope IS NULL OR access_scope IN ('inside', 'outside', 'unknown')");

            migrationBuilder.CreateIndex(
                name: "ix_project_access_policies_project_id",
                table: "project_access_policies",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_access_policies_signed_by",
                table: "project_access_policies",
                column: "signed_by");

            migrationBuilder.CreateIndex(
                name: "ix_project_access_policies_updated_by",
                table: "project_access_policies",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_project_access_policies_workspace_id",
                table: "project_access_policies",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_access_policy_keys_workspace_id_project_key",
                table: "project_access_policy_keys",
                columns: new[] { "workspace_id", "project_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_access_policy_keys");

            migrationBuilder.DropTable(
                name: "project_access_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_permission_requests_access_scope",
                table: "permission_requests");

            migrationBuilder.DropColumn(
                name: "enforce_access_policies",
                table: "workspace_settings");

            migrationBuilder.DropColumn(
                name: "access_scope",
                table: "permission_requests");

            migrationBuilder.DropColumn(
                name: "access_report",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "access_report_at",
                table: "agents");
        }
    }
}
