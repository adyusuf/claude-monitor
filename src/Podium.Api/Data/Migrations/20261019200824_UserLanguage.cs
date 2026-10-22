using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "users",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_language",
                table: "users",
                sql: "language IS NULL OR language IN ('en', 'tr', 'de')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_language",
                table: "users");

            migrationBuilder.DropColumn(
                name: "language",
                table: "users");
        }
    }
}
