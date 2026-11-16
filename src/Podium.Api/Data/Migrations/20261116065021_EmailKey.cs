using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-180: the identity key is the address trimmed and lower-cased with the Unicode simple mapping (pg_c_utf8), exactly as
            // EmailIdentity.Key does it. users.email is stored trimmed already. Folded addresses are unique (email_normalized), and lower-casing
            // folds less, so two keys cannot meet; should they anyway, the migration stops before it changes anything and changes no row.
            migrationBuilder.Sql("""
                DO $$
                DECLARE groups bigint;
                BEGIN
                    SELECT count(*) INTO groups FROM (
                        SELECT 1 FROM users WHERE btrim(email) <> '' GROUP BY lower(btrim(email) COLLATE pg_c_utf8) HAVING count(*) > 1) d;
                    IF groups > 0 THEN
                        RAISE EXCEPTION 'users: % e-mail identity keys are shared by more than one account; correct those addresses and run the migration again (nothing was changed)', groups;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "email_key",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("UPDATE users SET email_key = lower(btrim(email) COLLATE pg_c_utf8) WHERE btrim(email) <> '';");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_key",
                table: "users",
                column: "email_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_email_key",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_key",
                table: "users");
        }
    }
}
