using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalAgentKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "external_agent_kind",
                table: "agent",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.Sql(
                """
                UPDATE "agent"
                SET "external_agent_kind" = CASE LOWER("name")
                    WHEN 'claudecode' THEN 1
                    WHEN 'codex' THEN 2
                    WHEN 'pi' THEN 3
                    ELSE 0
                END
                WHERE "type" = 1;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "external_agent_kind", table: "agent");
        }
    }
}
