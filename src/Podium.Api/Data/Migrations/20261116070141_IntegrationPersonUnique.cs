using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationPersonUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-068: never delete or relink a person here. A member already linked to two persons of one connection stops the migration
            // before anything changes; an admin unlinks the extra person on the integration's People page and runs it again.
            migrationBuilder.Sql("""
                DO $$
                DECLARE groups bigint;
                BEGIN
                    SELECT count(*) INTO groups FROM (
                        SELECT 1 FROM integration_people WHERE user_id IS NOT NULL GROUP BY connection_id, user_id HAVING count(*) > 1) d;
                    IF groups > 0 THEN
                        RAISE EXCEPTION 'integration_people: % members are linked to more than one person of a connection; unlink the extra persons and run the migration again (nothing was changed)', groups;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_integration_people_user",
                table: "integration_people",
                columns: new[] { "connection_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_integration_people_user",
                table: "integration_people");
        }
    }
}
