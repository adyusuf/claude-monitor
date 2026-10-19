using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventorySwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "share_inventory",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "share_inventory_set_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "share_inventory_set_by",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_share_inventory_set_by",
                table: "agents",
                column: "share_inventory_set_by");

            migrationBuilder.AddForeignKey(
                name: "fk_agents_users_share_inventory_set_by",
                table: "agents",
                column: "share_inventory_set_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_users_share_inventory_set_by",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ix_agents_share_inventory_set_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "share_inventory",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "share_inventory_set_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "share_inventory_set_by",
                table: "agents");
        }
    }
}
