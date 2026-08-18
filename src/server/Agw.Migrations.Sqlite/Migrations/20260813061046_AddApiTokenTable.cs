using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agw.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTokenTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_token",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    prefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    secret_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    create_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    create_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    update_time = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    update_by = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_token", x => x.id);
                },
                comment: "Stores hashed API tokens used by external Agw clients."
            );

            migrationBuilder.CreateIndex(
                name: "ix_api_token_normalized_name",
                table: "api_token",
                column: "normalized_name",
                unique: true
            );

            migrationBuilder.CreateIndex(name: "ix_api_token_prefix", table: "api_token", column: "prefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "api_token");
        }
    }
}
