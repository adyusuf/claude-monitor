using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PermissionRequestKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_key",
                table: "permission_requests",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_permission_requests_agent_id_client_key",
                table: "permission_requests",
                columns: new[] { "agent_id", "client_key" },
                unique: true,
                filter: "client_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_permission_requests_agent_id_client_key",
                table: "permission_requests");

            migrationBuilder.DropColumn(
                name: "client_key",
                table: "permission_requests");
        }
    }
}
