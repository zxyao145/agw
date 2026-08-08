using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceBuildingBlocksWithTypedTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // SQLite column facets such as MaxLength are model metadata. Rebuilding
                // these tables only to change nullability would revalidate unrelated
                // historical foreign keys while copying rows into temporary tables.
                migrationBuilder.Sql(
                    """
                    UPDATE "project"
                    SET "tools" = '[]'
                    WHERE "tools" IS NULL;
                    """);
                migrationBuilder.Sql(
                    """
                    UPDATE "agent"
                    SET "tools" = '[]'
                    WHERE "tools" IS NULL;
                    """);
                migrationBuilder.Sql(
                    """ALTER TABLE "project" DROP COLUMN "building_blocks";""");
                migrationBuilder.Sql(
                    """ALTER TABLE "agent" DROP COLUMN "building_blocks";""");
                return;
            }

            migrationBuilder.DropColumn(
                name: "building_blocks",
                table: "project");

            migrationBuilder.DropColumn(
                name: "building_blocks",
                table: "agent");

            migrationBuilder.AlterColumn<string>(
                name: "tools",
                table: "project",
                type: "TEXT",
                maxLength: 16000,
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "tools",
                table: "agent",
                type: "TEXT",
                maxLength: 16000,
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 4000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql(
                    """ALTER TABLE "project" ADD COLUMN "building_blocks" TEXT NULL;""");
                migrationBuilder.Sql(
                    """ALTER TABLE "agent" ADD COLUMN "building_blocks" TEXT NULL;""");
                return;
            }

            migrationBuilder.AlterColumn<string>(
                name: "tools",
                table: "project",
                type: "TEXT",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16000);

            migrationBuilder.AddColumn<string>(
                name: "building_blocks",
                table: "project",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "tools",
                table: "agent",
                type: "TEXT",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16000);

            migrationBuilder.AddColumn<string>(
                name: "building_blocks",
                table: "agent",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);
        }
    }
}
