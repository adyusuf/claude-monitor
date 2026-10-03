using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_hash = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code_hash = table.Column<string>(type: "text", nullable: false),
                    user_code = table.Column<string>(type: "text", nullable: false),
                    machine_key_hash = table.Column<string>(type: "text", nullable: false),
                    requested_hostname = table.Column<string>(type: "text", nullable: false),
                    requested_os = table.Column<string>(type: "text", nullable: false),
                    requested_arch = table.Column<string>(type: "text", nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_polled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_authorizations", x => x.id);
                    table.CheckConstraint("ck_device_authorizations_status", "status IN ('pending', 'approved', 'denied', 'expired', 'consumed')");
                });

            migrationBuilder.CreateTable(
                name: "harness_kinds",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_harness_kinds", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    email_normalized = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    display_name_search = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    email_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_status", "status IN ('active', 'disabled', 'deleted')");
                });

            migrationBuilder.CreateTable(
                name: "login_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_login_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_subject = table.Column<string>(type: "text", nullable: false),
                    provider_email = table.Column<string>(type: "text", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => x.id);
                    table.CheckConstraint("ck_user_logins_provider", "provider IN ('github', 'google')");
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => x.id);
                    table.CheckConstraint("ck_user_tokens_purpose", "purpose IN ('verify_email', 'reset_password')");
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_search = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.id);
                    table.CheckConstraint("ck_workspaces_status", "status IN ('active', 'archived')");
                    table.ForeignKey(
                        name: "fk_workspaces_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "event_archives",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_archives", x => x.id);
                    table.ForeignKey(
                        name: "fk_event_archives_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "machines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_key_hash = table.Column<string>(type: "text", nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: false),
                    os = table.Column<string>(type: "text", nullable: false),
                    os_version = table.Column<string>(type: "text", nullable: true),
                    arch = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machines", x => x.id);
                    table.CheckConstraint("ck_machines_os", "os IN ('macos', 'windows')");
                    table.ForeignKey(
                        name: "fk_machines_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    display_name_search = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.ForeignKey(
                        name: "fk_projects_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_normalized = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_invitations", x => x.id);
                    table.CheckConstraint("ck_workspace_invitations_role", "role IN ('owner', 'admin', 'member', 'viewer')");
                    table.ForeignKey(
                        name: "fk_workspace_invitations_users_invited_by",
                        column: x => x.invited_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workspace_invitations_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_members",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_members", x => new { x.workspace_id, x.user_id });
                    table.CheckConstraint("ck_workspace_members_role", "role IN ('owner', 'admin', 'member', 'viewer')");
                    table.ForeignKey(
                        name: "fk_workspace_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workspace_members_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_settings",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mask_secrets = table.Column<bool>(type: "boolean", nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    event_max_bytes = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_settings", x => x.workspace_id);
                    table.CheckConstraint("ck_workspace_settings_retention", "retention_days BETWEEN 1 AND 3650");
                    table.ForeignKey(
                        name: "fk_workspace_settings_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.CheckConstraint("ck_agents_status", "status IN ('active', 'revoked')");
                    table.ForeignKey(
                        name: "fk_agents_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agents_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_batches",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_seq = table.Column<long>(type: "bigint", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_batches", x => new { x.agent_id, x.batch_seq });
                    table.ForeignKey(
                        name: "fk_agent_batches_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replaced_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_tokens", x => x.id);
                    table.CheckConstraint("ck_agent_tokens_kind", "kind IN ('access', 'refresh')");
                    table.ForeignKey(
                        name: "fk_agent_tokens_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "harness_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    harness_kind = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    title_search = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    git_branch = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_harness_sessions", x => x.id);
                    table.CheckConstraint("ck_harness_sessions_status", "status IN ('active', 'idle', 'waiting', 'ended')");
                    table.ForeignKey(
                        name: "fk_harness_sessions_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_harness_sessions_harness_kinds_harness_kind",
                        column: x => x.harness_kind,
                        principalTable: "harness_kinds",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_harness_sessions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_harness_sessions_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "permission_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "text", nullable: false),
                    tool_input = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    answered_by = table.Column<Guid>(type: "uuid", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permission_requests", x => x.id);
                    table.CheckConstraint("ck_permission_requests_decision", "decision IS NULL OR decision IN ('allow', 'deny')");
                    table.CheckConstraint("ck_permission_requests_status", "status IN ('open', 'answered', 'expired')");
                    table.ForeignKey(
                        name: "fk_permission_requests_harness_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_commands", x => x.id);
                    table.CheckConstraint("ck_session_commands_kind", "kind IN ('prompt', 'stop')");
                    table.CheckConstraint("ck_session_commands_status", "status IN ('queued', 'delivered', 'applied', 'failed', 'expired', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_session_commands_harness_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_commands_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // session_events is partitioned by month on received_at (docs/data-model.md §4); EF cannot express
            // PARTITION BY, so this table is written by hand. The default partition catches any month the
            // housekeeper has not created yet. Indexes below are created on the parent and reach every partition.
            migrationBuilder.Sql("""
                CREATE TABLE session_events (
                    id bigint GENERATED ALWAYS AS IDENTITY,
                    received_at timestamp with time zone NOT NULL,
                    session_id uuid NOT NULL,
                    workspace_id uuid NOT NULL,
                    kind text NOT NULL,
                    occurred_at timestamp with time zone NOT NULL,
                    payload jsonb NOT NULL,
                    truncated boolean NOT NULL,
                    CONSTRAINT pk_session_events PRIMARY KEY (id, received_at),
                    CONSTRAINT fk_session_events_harness_sessions_session_id FOREIGN KEY (session_id)
                        REFERENCES harness_sessions (id) ON DELETE RESTRICT
                ) PARTITION BY RANGE (received_at);
                CREATE TABLE session_events_default PARTITION OF session_events DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "session_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    subject_search = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_tasks", x => x.id);
                    table.CheckConstraint("ck_session_tasks_status", "status IN ('pending', 'in_progress', 'completed', 'deleted')");
                    table.ForeignKey(
                        name: "fk_session_tasks_harness_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_usage",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_usage", x => new { x.session_id, x.model });
                    table.ForeignKey(
                        name: "fk_session_usage_harness_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subagent_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    agent_type = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subagent_runs", x => x.id);
                    table.CheckConstraint("ck_subagent_runs_status", "status IN ('running', 'finished', 'failed')");
                    table.ForeignKey(
                        name: "fk_subagent_runs_harness_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "harness_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "harness_kinds",
                columns: new[] { "code", "added_at", "display_name" },
                values: new object[] { "claude_code", new DateTimeOffset(new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Claude Code" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_tokens_agent_id",
                table: "agent_tokens",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_tokens_token_hash",
                table: "agent_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_machine_id_user_id",
                table: "agents",
                columns: new[] { "machine_id", "user_id" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_agents_user_id",
                table: "agents",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_workspace_id",
                table: "agents",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id_at",
                table: "audit_events",
                columns: new[] { "actor_user_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_workspace_id_at",
                table: "audit_events",
                columns: new[] { "workspace_id", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_device_authorizations_device_code_hash",
                table: "device_authorizations",
                column: "device_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_authorizations_user_code",
                table: "device_authorizations",
                column: "user_code",
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_event_archives_workspace_id_day",
                table: "event_archives",
                columns: new[] { "workspace_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_harness_sessions_agent_id_harness_kind_external_id",
                table: "harness_sessions",
                columns: new[] { "agent_id", "harness_kind", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_harness_sessions_harness_kind",
                table: "harness_sessions",
                column: "harness_kind");

            migrationBuilder.CreateIndex(
                name: "ix_harness_sessions_project_id_last_event_at",
                table: "harness_sessions",
                columns: new[] { "project_id", "last_event_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_harness_sessions_workspace_id_last_event_at_id",
                table: "harness_sessions",
                columns: new[] { "workspace_id", "last_event_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_login_sessions_token_hash",
                table: "login_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_sessions_user_id",
                table: "login_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_machines_workspace_id_machine_key_hash",
                table: "machines",
                columns: new[] { "workspace_id", "machine_key_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_permission_requests_session_id_status",
                table: "permission_requests",
                columns: new[] { "session_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_workspace_id_key",
                table: "projects",
                columns: new[] { "workspace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_commands_agent_id_status",
                table: "session_commands",
                columns: new[] { "agent_id", "status" },
                filter: "status IN ('queued', 'delivered')");

            migrationBuilder.CreateIndex(
                name: "ix_session_commands_created_by",
                table: "session_commands",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_session_commands_session_id_created_at",
                table: "session_commands",
                columns: new[] { "session_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_session_events_session_id_occurred_at",
                table: "session_events",
                columns: new[] { "session_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_events_workspace_id_received_at",
                table: "session_events",
                columns: new[] { "workspace_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_session_tasks_session_id_external_id",
                table: "session_tasks",
                columns: new[] { "session_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subagent_runs_session_id_external_id",
                table: "subagent_runs",
                columns: new[] { "session_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_provider_provider_subject",
                table: "user_logins",
                columns: new[] { "provider", "provider_subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_token_hash",
                table: "user_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_user_id",
                table: "user_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_normalized",
                table: "users",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_invitations_invited_by",
                table: "workspace_invitations",
                column: "invited_by");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_invitations_token_hash",
                table: "workspace_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workspace_invitations_workspace_id",
                table: "workspace_invitations",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_members_user_id",
                table: "workspace_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_created_by",
                table: "workspaces",
                column: "created_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_batches");

            migrationBuilder.DropTable(
                name: "agent_tokens");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "device_authorizations");

            migrationBuilder.DropTable(
                name: "event_archives");

            migrationBuilder.DropTable(
                name: "login_sessions");

            migrationBuilder.DropTable(
                name: "permission_requests");

            migrationBuilder.DropTable(
                name: "session_commands");

            migrationBuilder.DropTable(
                name: "session_events");

            migrationBuilder.DropTable(
                name: "session_tasks");

            migrationBuilder.DropTable(
                name: "session_usage");

            migrationBuilder.DropTable(
                name: "subagent_runs");

            migrationBuilder.DropTable(
                name: "user_logins");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "workspace_invitations");

            migrationBuilder.DropTable(
                name: "workspace_members");

            migrationBuilder.DropTable(
                name: "workspace_settings");

            migrationBuilder.DropTable(
                name: "harness_sessions");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "harness_kinds");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "machines");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
