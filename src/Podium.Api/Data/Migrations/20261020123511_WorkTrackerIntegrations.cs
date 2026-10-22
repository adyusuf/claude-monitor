using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkTrackerIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_sprints_active_application",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "ux_sprints_active_project",
                table: "sprints");

            migrationBuilder.AddColumn<Guid>(
                name: "source_connection_id",
                table: "sprints",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_connection_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "assignee_person_id",
                table: "issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_key",
                table: "issues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_url",
                table: "issues",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "integration_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    site_host = table.Column<string>(type: "text", nullable: false),
                    cloud_id = table.Column<string>(type: "text", nullable: false),
                    connector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    auto_invite = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    webhook_ids = table.Column<string>(type: "jsonb", nullable: true),
                    webhook_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_pass_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connections", x => x.id);
                    table.CheckConstraint("ck_integration_connections_provider", "provider IN ('jira')");
                    table.CheckConstraint("ck_integration_connections_status", "status IN ('active', 'reauthorize', 'disconnected')");
                    table.ForeignKey(
                        name: "fk_integration_connections_users_connector_user_id",
                        column: x => x.connector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_integration_connections_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_integration_connections_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_account_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    refresh_token = table.Column<string>(type: "text", nullable: false),
                    access_token = table.Column<string>(type: "text", nullable: false),
                    access_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_accounts", x => x.id);
                    table.CheckConstraint("ck_integration_accounts_status", "status IN ('active', 'reauthorize')");
                    table.ForeignKey(
                        name: "fk_integration_accounts_integration_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_integration_accounts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    external_key = table.Column<string>(type: "text", nullable: true),
                    local_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_updated = table.Column<string>(type: "text", nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_links", x => x.id);
                    table.CheckConstraint("ck_integration_links_kind", "kind IN ('project', 'issue', 'sprint')");
                    table.ForeignKey(
                        name: "fk_integration_links_integration_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_oauth_states",
                columns: table => new
                {
                    state_hash = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    site_host = table.Column<string>(type: "text", nullable: false),
                    verifier = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_oauth_states", x => x.state_hash);
                    table.CheckConstraint("ck_integration_oauth_states_purpose", "purpose IN ('connect', 'account')");
                    table.ForeignKey(
                        name: "fk_integration_oauth_states_integration_connections_connection~",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_integration_oauth_states_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_integration_oauth_states_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_people",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_account_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    email_normalized = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_people", x => x.id);
                    table.ForeignKey(
                        name: "fk_integration_people_integration_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_integration_people_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    archived = table.Column<int>(type: "integer", nullable: false),
                    people = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_sync_runs", x => x.id);
                    table.CheckConstraint("ck_integration_sync_runs_kind", "kind IN ('import', 'webhook', 'pass')");
                    table.CheckConstraint("ck_integration_sync_runs_status", "status IN ('running', 'ok', 'partial', 'failed')");
                    table.ForeignKey(
                        name: "fk_integration_sync_runs_integration_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_webhook_receipts",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_webhook_receipts", x => new { x.connection_id, x.delivery_id });
                    table.ForeignKey(
                        name: "fk_integration_webhook_receipts_integration_connections_connec~",
                        column: x => x.connection_id,
                        principalTable: "integration_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sprints_source_connection_id",
                table: "sprints",
                column: "source_connection_id");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_application",
                table: "sprints",
                column: "application_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NOT NULL AND source_connection_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_project",
                table: "sprints",
                column: "project_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NULL AND source_connection_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_projects_source_connection_id",
                table: "projects",
                column: "source_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_assignee_person_id",
                table: "issues",
                column: "assignee_person_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_external_key",
                table: "issues",
                column: "external_key");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issues_external_url",
                table: "issues",
                sql: "external_url IS NULL OR external_url LIKE 'https://%'");

            migrationBuilder.CreateIndex(
                name: "ix_integration_accounts_connection_id_user_id",
                table: "integration_accounts",
                columns: new[] { "connection_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_accounts_user_id",
                table: "integration_accounts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connections_connector_user_id",
                table: "integration_connections",
                column: "connector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connections_created_by",
                table: "integration_connections",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_integration_connections_workspace_id",
                table: "integration_connections",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_integration_connections_site",
                table: "integration_connections",
                columns: new[] { "workspace_id", "provider", "site_host" },
                unique: true,
                filter: "status <> 'disconnected'");

            migrationBuilder.CreateIndex(
                name: "ix_integration_links_connection_id_kind_external_id",
                table: "integration_links",
                columns: new[] { "connection_id", "kind", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_links_local_id",
                table: "integration_links",
                column: "local_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_oauth_states_connection_id",
                table: "integration_oauth_states",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_oauth_states_expires_at",
                table: "integration_oauth_states",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_integration_oauth_states_user_id",
                table: "integration_oauth_states",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_oauth_states_workspace_id",
                table: "integration_oauth_states",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_people_connection_id_external_account_id",
                table: "integration_people",
                columns: new[] { "connection_id", "external_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_people_email_normalized",
                table: "integration_people",
                column: "email_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_integration_people_user_id",
                table: "integration_people",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_integration_sync_runs_connection_id_started_at",
                table: "integration_sync_runs",
                columns: new[] { "connection_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_webhook_receipts_received_at",
                table: "integration_webhook_receipts",
                column: "received_at");

            migrationBuilder.AddForeignKey(
                name: "fk_issues_integration_people_assignee_person_id",
                table: "issues",
                column: "assignee_person_id",
                principalTable: "integration_people",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_integration_connections_source_connection_id",
                table: "projects",
                column: "source_connection_id",
                principalTable: "integration_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sprints_integration_connections_source_connection_id",
                table: "sprints",
                column: "source_connection_id",
                principalTable: "integration_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_issues_integration_people_assignee_person_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_integration_connections_source_connection_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_sprints_integration_connections_source_connection_id",
                table: "sprints");

            migrationBuilder.DropTable(
                name: "integration_accounts");

            migrationBuilder.DropTable(
                name: "integration_links");

            migrationBuilder.DropTable(
                name: "integration_oauth_states");

            migrationBuilder.DropTable(
                name: "integration_people");

            migrationBuilder.DropTable(
                name: "integration_sync_runs");

            migrationBuilder.DropTable(
                name: "integration_webhook_receipts");

            migrationBuilder.DropTable(
                name: "integration_connections");

            migrationBuilder.DropIndex(
                name: "ix_sprints_source_connection_id",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "ux_sprints_active_application",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "ux_sprints_active_project",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "ix_projects_source_connection_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_issues_assignee_person_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issues_external_key",
                table: "issues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_issues_external_url",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "source_connection_id",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "source_connection_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "assignee_person_id",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "external_key",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "external_url",
                table: "issues");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_application",
                table: "sprints",
                column: "application_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_project",
                table: "sprints",
                column: "project_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NULL");
        }
    }
}
