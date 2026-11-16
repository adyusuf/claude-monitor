using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SprintActiveIndexArchived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-004: an archived sprint stops holding its scope's active slot. The new filters only leave rows out, so this cannot fail.
            migrationBuilder.DropIndex(
                name: "ux_sprints_active_application",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "ux_sprints_active_project",
                table: "sprints");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_application",
                table: "sprints",
                column: "application_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NOT NULL AND source_connection_id IS NULL AND archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sprints_active_project",
                table: "sprints",
                column: "project_id",
                unique: true,
                filter: "status = 'active' AND application_id IS NULL AND source_connection_id IS NULL AND archived_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old filters count an archived active sprint too, so a scope that has one next to a live active sprint cannot get them
            // back. Nothing is closed or deleted here: the roll-back stops before anything changes and names the count.
            migrationBuilder.Sql("""
                DO $$
                DECLARE scopes bigint;
                BEGIN
                    SELECT count(*) INTO scopes FROM (
                        SELECT 1 FROM sprints WHERE status = 'active' AND source_connection_id IS NULL
                        GROUP BY project_id, application_id HAVING count(*) > 1) d;
                    IF scopes > 0 THEN
                        RAISE EXCEPTION 'sprints: % projects or applications have an archived active sprint next to their active one; close the archived sprints and roll back again (nothing was changed)', scopes;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_sprints_active_application",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "ux_sprints_active_project",
                table: "sprints");

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
        }
    }
}
