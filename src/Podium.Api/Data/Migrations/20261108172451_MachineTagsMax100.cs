using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineTagsMax100 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_tags",
                table: "agents");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_tags",
                table: "agents",
                sql: "tags IS NULL OR (jsonb_typeof(tags) = 'object' AND jsonb_array_length(jsonb_path_query_array(tags, '$.keyvalue()')) <= 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_tags",
                table: "agents");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_tags",
                table: "agents",
                sql: "tags IS NULL OR (jsonb_typeof(tags) = 'object' AND jsonb_array_length(jsonb_path_query_array(tags, '$.keyvalue()')) <= 10)");
        }
    }
}
