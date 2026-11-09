using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PermissionRequestRepo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "repo_id",
                table: "permission_requests",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "repo_id",
                table: "permission_requests");
        }
    }
}
