using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillKindAndRemoteCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";

            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "skill",
                type: isPostgres ? "integer" : "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "remote_url",
                table: "skill",
                type: isPostgres ? "character varying(2048)" : "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.Sql(
                isPostgres
                    ? """
                      UPDATE skill
                      SET kind = 0
                      WHERE id = '11111111-1111-1111-8888-000000000002'::uuid;
                      """
                    : """
                      UPDATE skill
                      SET kind = 0
                      WHERE lower(id) = '11111111-1111-1111-8888-000000000002';
                      """);

            migrationBuilder.CreateTable(
                name: "remote_skill_cache",
                columns: table => new
                {
                    skill_id = table.Column<Guid>(
                        type: isPostgres ? "uuid" : "TEXT",
                        nullable: false),
                    source_url = table.Column<string>(
                        type: isPostgres ? "character varying(2048)" : "TEXT",
                        maxLength: 2048,
                        nullable: false),
                    content_json = table.Column<string>(
                        type: isPostgres ? "text" : "TEXT",
                        nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(
                        type: isPostgres ? "timestamp with time zone" : "TEXT",
                        nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_skill_cache", x => x.skill_id);
                    table.ForeignKey(
                        name: "fk_remote_skill_cache_skill_skill_id",
                        column: x => x.skill_id,
                        principalTable: "skill",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remote_skill_cache");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "skill");

            migrationBuilder.DropColumn(
                name: "remote_url",
                table: "skill");
        }
    }
}
