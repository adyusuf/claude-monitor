using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_user_tokens_purpose",
                table: "user_tokens");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "totp_enabled_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "totp_last_step",
                table: "users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "totp_secret",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_tokens_purpose",
                table: "user_tokens",
                sql: "purpose IN ('verify_email', 'reset_password', 'mfa_pending')");

            migrationBuilder.CreateIndex(
                name: "ix_user_recovery_codes_code_hash",
                table: "user_recovery_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_recovery_codes_user_id",
                table: "user_recovery_codes",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_recovery_codes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_tokens_purpose",
                table: "user_tokens");

            migrationBuilder.DropColumn(
                name: "totp_enabled_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_last_step",
                table: "users");

            migrationBuilder.DropColumn(
                name: "totp_secret",
                table: "users");

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_tokens_purpose",
                table: "user_tokens",
                sql: "purpose IN ('verify_email', 'reset_password')");
        }
    }
}
