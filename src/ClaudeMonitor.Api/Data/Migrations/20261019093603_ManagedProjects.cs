using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_search = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sponsor = table.Column<string>(type: "text", nullable: true),
                    stakeholders = table.Column<string>(type: "jsonb", nullable: true),
                    budget_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    budget_currency = table.Column<string>(type: "text", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    item_types = table.Column<string[]>(type: "text[]", nullable: false),
                    priority_scale = table.Column<string>(type: "text", nullable: false, defaultValue: "p0_p3"),
                    estimation_unit = table.Column<string>(type: "text", nullable: false, defaultValue: "story_points"),
                    sprint_length_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 14),
                    definition_of_ready = table.Column<string[]>(type: "text[]", nullable: false),
                    definition_of_done = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.CheckConstraint("ck_projects_budget", "(budget_amount IS NULL) = (budget_currency IS NULL) AND (budget_amount IS NULL OR budget_amount >= 0)");
                    table.CheckConstraint("ck_projects_checklists", "cardinality(definition_of_ready) <= 30 AND cardinality(definition_of_done) <= 30");
                    table.CheckConstraint("ck_projects_dates", "end_date IS NULL OR start_date IS NULL OR end_date >= start_date");
                    table.CheckConstraint("ck_projects_estimation_unit", "estimation_unit IN ('story_points', 'hours', 'days', 't_shirt')");
                    table.CheckConstraint("ck_projects_item_types", "cardinality(item_types) >= 1 AND item_types <@ ARRAY['epic', 'story', 'task', 'bug', 'spike']::text[]");
                    table.CheckConstraint("ck_projects_priority_scale", "priority_scale IN ('p0_p3', 'moscow', 'high_medium_low', 'one_to_five')");
                    table.CheckConstraint("ck_projects_sprint_length", "sprint_length_days BETWEEN 1 AND 42");
                    table.CheckConstraint("ck_projects_stakeholders", "stakeholders IS NULL OR (jsonb_typeof(stakeholders) = 'array' AND jsonb_array_length(stakeholders) <= 50)");
                    table.CheckConstraint("ck_projects_status", "status IN ('active', 'archived')");
                    table.ForeignKey(
                        name: "fk_projects_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_projects_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_projects_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_projects_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_search = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false, defaultValue: "web"),
                    repo_url = table.Column<string>(type: "text", nullable: true),
                    environments = table.Column<string>(type: "jsonb", nullable: true),
                    tech_stack = table.Column<string[]>(type: "text[]", nullable: false),
                    versioning_scheme = table.Column<string>(type: "text", nullable: false, defaultValue: "semver"),
                    tech_owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    non_functional_targets = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                    table.CheckConstraint("ck_applications_environments", "environments IS NULL OR (jsonb_typeof(environments) = 'array' AND jsonb_array_length(environments) <= 3)");
                    table.CheckConstraint("ck_applications_kind", "kind IN ('web', 'mobile', 'api', 'other')");
                    table.CheckConstraint("ck_applications_status", "status IN ('active', 'archived')");
                    table.CheckConstraint("ck_applications_targets", "non_functional_targets IS NULL OR (jsonb_typeof(non_functional_targets) = 'array' AND jsonb_array_length(non_functional_targets) <= 20)");
                    table.CheckConstraint("ck_applications_tech_stack", "cardinality(tech_stack) <= 30");
                    table.CheckConstraint("ck_applications_versioning_scheme", "versioning_scheme IN ('semver', 'calver', 'none')");
                    table.ForeignKey(
                        name: "fk_applications_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_applications_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_applications_users_tech_owner_user_id",
                        column: x => x.tech_owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_applications_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_applications_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_members", x => new { x.project_id, x.user_id });
                    table.CheckConstraint("ck_project_members_role", "role IN ('manager', 'contributor', 'reviewer', 'viewer')");
                    table.ForeignKey(
                        name: "fk_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_members_users_added_by",
                        column: x => x.added_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_project_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_components",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_search = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false, defaultValue: "component"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_components", x => x.id);
                    table.CheckConstraint("ck_application_components_kind", "kind IN ('component', 'module')");
                    table.CheckConstraint("ck_application_components_status", "status IN ('active', 'archived')");
                    table.ForeignKey(
                        name: "fk_application_components_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_application_components_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    name_search = table.Column<string>(type: "text", nullable: false),
                    planned_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "planned"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_releases", x => x.id);
                    table.CheckConstraint("ck_application_releases_status", "status IN ('planned', 'released', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_application_releases_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_application_releases_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_components_application_id_name_search",
                table: "application_components",
                columns: new[] { "application_id", "name_search" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_application_components_workspace_id",
                table: "application_components",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_releases_application_id_name_search",
                table: "application_releases",
                columns: new[] { "application_id", "name_search" },
                unique: true,
                filter: "status <> 'cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_application_releases_workspace_id",
                table: "application_releases",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_applications_created_by",
                table: "applications",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_applications_project_id_created_at_id",
                table: "applications",
                columns: new[] { "project_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_applications_project_id_name_search",
                table: "applications",
                columns: new[] { "project_id", "name_search" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_applications_tech_owner_user_id",
                table: "applications",
                column: "tech_owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_applications_updated_by",
                table: "applications",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_applications_workspace_id",
                table: "applications",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_added_by",
                table: "project_members",
                column: "added_by");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_user_id",
                table: "project_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_created_by",
                table: "projects",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_projects_owner_user_id",
                table: "projects",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_updated_by",
                table: "projects",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_projects_workspace_id_created_at_id",
                table: "projects",
                columns: new[] { "workspace_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_projects_workspace_id_name_search",
                table: "projects",
                columns: new[] { "workspace_id", "name_search" },
                unique: true,
                filter: "status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_components");

            migrationBuilder.DropTable(
                name: "application_releases");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
