using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailNormalizedNonUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-180: identity is email_key (unique); the folded address stays a search index, so "josé@" and "jose@" can be two accounts.
            // Dropping a uniqueness cannot fail.
            migrationBuilder.DropIndex(
                name: "ix_users_email_normalized",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_normalized",
                table: "users",
                column: "email_normalized");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Two accounts that fold to one address cannot get the old unique index back. Nothing is deleted or merged here: the roll-back
            // stops before anything changes and names the count.
            migrationBuilder.Sql("""
                DO $$
                DECLARE groups bigint;
                BEGIN
                    SELECT count(*) INTO groups FROM (
                        SELECT 1 FROM users WHERE email_normalized IS NOT NULL GROUP BY email_normalized HAVING count(*) > 1) d;
                    IF groups > 0 THEN
                        RAISE EXCEPTION 'users: % normalised e-mail addresses are shared by more than one account; correct those addresses and roll back again (nothing was changed)', groups;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_users_email_normalized",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_normalized",
                table: "users",
                column: "email_normalized",
                unique: true);
        }
    }
}
