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
            // The old CHECK refuses a machine with more than 10 tags, so those keep their first 10 keys (in key order) before it comes back.
            migrationBuilder.Sql("""
                UPDATE agents SET tags = (SELECT jsonb_object_agg(e.key, e.value) FROM (SELECT key, value FROM jsonb_each(agents.tags) ORDER BY key LIMIT 10) e)
                WHERE jsonb_typeof(tags) = 'object' AND (SELECT count(*) FROM jsonb_each(agents.tags)) > 10;
                """);

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
