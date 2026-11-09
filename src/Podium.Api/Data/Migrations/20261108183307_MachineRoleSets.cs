using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Podium.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachineRoleSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "role_environments",
                table: "agents",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "role_functions",
                table: "agents",
                type: "text[]",
                nullable: true);

            // A role set before sets existed becomes a set of one; the single columns keep their value (the strictest of a set of one is itself).
            migrationBuilder.Sql("""
                UPDATE agents SET role_environments = ARRAY[role_environment] WHERE role_environment IS NOT NULL;
                UPDATE agents SET role_functions = ARRAY[role_function] WHERE role_function IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_role_environments",
                table: "agents",
                sql: "role_environments IS NULL OR role_environments <@ ARRAY['dev', 'test', 'stage', 'preprod', 'production', 'other']::text[]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agents_role_functions",
                table: "agents",
                sql: "role_functions IS NULL OR role_functions <@ ARRAY['development', 'application', 'web', 'database', 'ci', 'storage', 'other']::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_role_environments",
                table: "agents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agents_role_functions",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role_environments",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role_functions",
                table: "agents");
        }
    }
}
