using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationAccountUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-080, F-337: never delete an account here. One Atlassian account already linked by two people of a connection stops the
            // migration before anything changes; one of them unlinks their account and it is run again.
            migrationBuilder.Sql("""
                DO $$
                DECLARE groups bigint;
                BEGIN
                    SELECT count(*) INTO groups FROM (
                        SELECT 1 FROM integration_accounts GROUP BY connection_id, external_account_id HAVING count(*) > 1) d;
                    IF groups > 0 THEN
                        RAISE EXCEPTION 'integration_accounts: % Atlassian accounts are linked by more than one person of a connection; unlink the extra links and run the migration again (nothing was changed)', groups;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_integration_accounts_account",
                table: "integration_accounts",
                columns: new[] { "connection_id", "external_account_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_integration_accounts_account",
                table: "integration_accounts");
        }
    }
}
