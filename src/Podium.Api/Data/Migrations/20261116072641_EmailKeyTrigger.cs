using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailKeyTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // F-180: an account written by a release that does not know email_key (the old one while a deploy migrates, or after a
            // roll-back) still gets its key, with the backfill's expression. A key the writer set is kept; a blank address has none; an
            // address changed without its key (an older release) gets the key of the new address.
            migrationBuilder.Sql("""
                CREATE FUNCTION users_email_key_fill() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.email IS NULL OR btrim(NEW.email) = '' THEN
                        NEW.email_key := NULL;
                    ELSIF NEW.email_key IS NULL
                        OR (TG_OP = 'UPDATE' AND NEW.email IS DISTINCT FROM OLD.email AND NEW.email_key IS NOT DISTINCT FROM OLD.email_key) THEN
                        NEW.email_key := lower(btrim(NEW.email) COLLATE pg_c_utf8);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER users_email_key_fill BEFORE INSERT OR UPDATE OF email ON users FOR EACH ROW EXECUTE FUNCTION users_email_key_fill();
                """);

            // Accounts made since EmailKey by a release without the column: filled once, the same way, or stopped before anything changes.
            migrationBuilder.Sql("""
                DO $$
                DECLARE groups bigint;
                BEGIN
                    SELECT count(*) INTO groups FROM (
                        SELECT 1 FROM users WHERE btrim(email) <> ''
                        GROUP BY coalesce(email_key, lower(btrim(email) COLLATE pg_c_utf8)) HAVING count(*) > 1) d;
                    IF groups > 0 THEN
                        RAISE EXCEPTION 'users: % e-mail identity keys are shared by more than one account; correct those addresses and run the migration again (nothing was changed)', groups;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("UPDATE users SET email_key = lower(btrim(email) COLLATE pg_c_utf8) WHERE email_key IS NULL AND btrim(email) <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER users_email_key_fill ON users;
                DROP FUNCTION users_email_key_fill();
                """);
        }
    }
}
